using System.Text.Json;
using Quasar.Client.Fleet;
using Quasar.Models;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Services.Auth;

namespace Quasar.Services.Plugins;

public static class QuasarFleetConsoleOperations
{
    private const int ServerLogWindowLines = 500;
    private static readonly QuasarOperationMetadata Read = new() { Effect = QuasarOperationEffect.Read, AgentAccess = QuasarAgentAccess.Denied, RequireDefaultPolicies = true };
    public static void AddQuasarFleetConsoleOperations(this IServiceCollection services)
    {
        services.AddQuasarOperation<ConsoleLogRequest, ConsoleFiles>(QuasarCoreOperations.PluginId, "fleet.console.files", [QuasarPolicyNames.CanView],
            (caller, input, _) => Task.FromResult(List(caller, input)), Read with { Description = "Lists available Dedicated Server or Magnetar log files for a configured standalone server." });
        services.AddQuasarRead<ConsoleLogRequest>(QuasarCoreOperations.PluginId, "fleet.console.read", [QuasarPolicyNames.CanView],
            (caller, input, _) =>
            {
                var selected = List(caller, input).Files.FirstOrDefault(f => f.FileName == input.Name);
                if (selected is null) throw new QuasarOperationException(404, "The selected log file no longer exists.");
                return Task.FromResult(new QuasarRead(async token =>
                {
                    object result = input.Mode switch
                    {
                        "latest" => await ReadLatestLineWindowAsync(selected.Path, ServerLogWindowLines, token),
                        "window" => await ReadLineWindowAsync(selected.Path, Math.Max(0, input.Start), ServerLogWindowLines, token),
                        "lines" => await ReadAppendedLinesAsync(selected.Path, input.Offset, token),
                        "text" => await ReadAppendedTextAsync(selected.Path, input.Offset, token),
                        "tail" => await ReadTailAsync(selected.Path, Math.Clamp(input.Max, 1, 4 * 1024 * 1024), token),
                        _ => throw new QuasarOperationException(400, "Unknown console read mode."),
                    };
                    return new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(result, JsonSerializerOptions.Web), writable: false);
                }, "application/json"));
            }, Read with { Description = "Streams a selected log window, tail or appended lines." });
    }

    private static ConsoleFiles List(QuasarOperationContext caller, ConsoleLogRequest input)
    {
        var def = caller.Services.GetRequiredService<DedicatedServerCatalog>().GetServer(input.Server) ?? throw new QuasarOperationException(404, "Server not found.");
        var paths = DedicatedServerPathResolver.Resolve(def);
        var (directory, pattern) = input.Kind switch
        {
            "server" => (paths.DedicatedServerAppDataPath, "SpaceEngineersDedicated*.log"),
            "magnetar" => (paths.MagnetarAppDataPath, "info*.log"),
            _ => throw new QuasarOperationException(400, "Choose the server or Magnetar log."),
        };
        if (!Directory.Exists(directory)) return new(directory, []);
        var files = new DirectoryInfo(directory).EnumerateFiles(pattern).Where(f => !f.Attributes.HasFlag(FileAttributes.ReparsePoint))
            .OrderByDescending(f => f.LastWriteTimeUtc).ThenByDescending(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Select(f => new LogFileInfo(f.FullName, f.Name, f.Length, f.LastWriteTimeUtc)).ToArray();
        return new(directory, files);
    }

    private static async Task<LogTailSnapshot> ReadTailAsync(string path, int maxBytes, CancellationToken token)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        var fileLength = stream.Length;
        var truncated = false;
        if (fileLength > maxBytes)
        {
            stream.Seek(-maxBytes, SeekOrigin.End);
            truncated = true;
        }

        using var reader = new StreamReader(stream);
        if (truncated)
        {
            // Drop the partial first line so output starts at a clean boundary.
            await reader.ReadLineAsync(token);
        }

        var text = await reader.ReadToEndAsync(token);
        return new LogTailSnapshot(text, truncated, fileLength);
    }

    private static async Task<ServerLogAppend> ReadAppendedLinesAsync(string path, long startOffset, CancellationToken token)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        var fileLength = stream.Length;
        if (startOffset < 0 || startOffset > fileLength || fileLength - startOffset > 4 * 1024 * 1024)
            return new ServerLogAppend(Array.Empty<string>(), fileLength, Reset: true);

        if (startOffset == fileLength)
            return new ServerLogAppend(Array.Empty<string>(), fileLength, Reset: false);

        stream.Seek(Math.Max(0, startOffset), SeekOrigin.Begin);
        using var reader = new StreamReader(stream);

        var lines = new List<string>();
        string? line;
        while ((line = await reader.ReadLineAsync(token)) is not null)
            lines.Add(line);

        return new ServerLogAppend(lines, fileLength, Reset: false);
    }

    private static async Task<LogTextAppend> ReadAppendedTextAsync(string path, long startOffset, CancellationToken token)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        var fileLength = stream.Length;
        if (startOffset < 0 || startOffset > fileLength || fileLength - startOffset > 4 * 1024 * 1024)
            return new LogTextAppend(string.Empty, fileLength, Reset: true);

        if (startOffset == fileLength)
            return new LogTextAppend(string.Empty, fileLength, Reset: false);

        stream.Seek(Math.Max(0, startOffset), SeekOrigin.Begin);
        using var reader = new StreamReader(stream);
        return new LogTextAppend(await reader.ReadToEndAsync(token), fileLength, Reset: false);
    }

    private static async Task<ServerLogLineWindow> ReadLatestLineWindowAsync(string path, int maxLines, CancellationToken token)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);

        var lines = new Queue<string>(maxLines + 1);
        var totalLines = 0;
        string? line;
        while ((line = await reader.ReadLineAsync(token)) is not null)
        {
            totalLines++;
            lines.Enqueue(line);
            if (lines.Count > maxLines)
                lines.Dequeue();
        }

        return new ServerLogLineWindow(lines.ToArray(), Math.Max(0, totalLines - lines.Count), totalLines, stream.Length);
    }

    private static async Task<ServerLogLineWindow> ReadLineWindowAsync(string path, int startLine, int maxLines, CancellationToken token)
    {
        var window = await ReadLineWindowCoreAsync(path, startLine, maxLines, token);
        var normalizedStart = Math.Clamp(startLine, 0, Math.Max(0, window.TotalLineCount - maxLines));
        return normalizedStart == startLine
            ? window
            : await ReadLineWindowCoreAsync(path, normalizedStart, maxLines, token);
    }

    private static async Task<ServerLogLineWindow> ReadLineWindowCoreAsync(string path, int startLine, int maxLines, CancellationToken token)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);

        var lines = new List<string>(maxLines);
        var currentLine = 0;
        string? line;
        while ((line = await reader.ReadLineAsync(token)) is not null)
        {
            if (currentLine >= startLine && lines.Count < maxLines)
                lines.Add(line);

            currentLine++;
        }

        return new ServerLogLineWindow(lines, startLine, currentLine, stream.Length);
    }

}
