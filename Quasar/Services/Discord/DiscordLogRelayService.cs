using Discord;
using Discord.WebSocket;
using Magnetar.Protocol.Runtime;

namespace Quasar.Services.Discord;

public sealed class DiscordLogRelayService
{
    private const int ChunkSize = 1900;
    private readonly object _sync = new();
    private readonly DedicatedServerSupervisor _supervisor;
    private readonly DedicatedServerCatalog _catalog;
    private readonly DiscordRateLimiter _rateLimiter;
    private readonly ILogger<DiscordLogRelayService> _logger;
    private readonly Dictionary<string, LogCursorState> _offsets = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Task> _tasks = [];

    public DiscordLogRelayService(
        DedicatedServerSupervisor supervisor,
        DedicatedServerCatalog catalog,
        DiscordRateLimiter rateLimiter,
        ILogger<DiscordLogRelayService> logger)
    {
        _supervisor = supervisor;
        _catalog = catalog;
        _rateLimiter = rateLimiter;
        _logger = logger;
    }

    public Task StartAsync(DiscordSocketClient client, DiscordOptions options, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            _tasks.Clear();

            foreach (var serverOptions in options.Servers.Where(server =>
                         server.EnableLogExport &&
                         server.LogChannelId.HasValue))
            {
                var cloned = serverOptions.Clone();
                _tasks.Add(Task.Run(() => RunLoopAsync(client, cloned, cancellationToken), CancellationToken.None));
            }
        }

        return Task.CompletedTask;
    }

    public void Reset()
    {
        lock (_sync)
        {
            _offsets.Clear();
            _tasks.Clear();
        }
    }

    private async Task RunLoopAsync(DiscordSocketClient client, DiscordServerOptions serverOptions, CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Max(1, serverOptions.LogExportIntervalMinutes)));
            while (await timer.WaitForNextTickAsync(cancellationToken))
                await ExportAsync(client, serverOptions, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ExportAsync(DiscordSocketClient client, DiscordServerOptions serverOptions, CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = _supervisor.GetSnapshots()
                .FirstOrDefault(item => string.Equals(item.UniqueName, serverOptions.UniqueName, StringComparison.OrdinalIgnoreCase));
            if (snapshot is null)
                return;

            var logPath = ResolveLatestDedicatedServerLogPath(serverOptions.UniqueName);
            if (string.IsNullOrWhiteSpace(logPath))
                return;

            var delta = await ReadDeltaAsync(serverOptions.UniqueName, logPath, cancellationToken);
            if (string.IsNullOrWhiteSpace(delta))
                return;

            if (client.GetChannel(serverOptions.LogChannelId!.Value) is not IMessageChannel channel)
                return;

            foreach (var chunk in ChunkText(delta, ChunkSize))
            {
                var codeBlock = $"```\n{EscapeCodeBlock(chunk)}\n```";
                await _rateLimiter.RunAsync(serverOptions.LogChannelId.Value, () => channel.SendMessageAsync(text: codeBlock), cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Discord log export failed for server {UniqueName}", serverOptions.UniqueName);
        }
    }

    private string? ResolveLatestDedicatedServerLogPath(string uniqueName)
    {
        var server = _catalog.GetServer(uniqueName);
        if (server is null)
            return null;

        var appDataPath = string.IsNullOrWhiteSpace(server.DedicatedServerAppDataPath)
            ? MagnetarPaths.GetQuasarServerDedicatedServerAppDataDirectory(uniqueName)
            : server.DedicatedServerAppDataPath.Trim();

        if (!Directory.Exists(appDataPath))
            return null;

        try
        {
            return Directory.EnumerateFiles(appDataPath, "SpaceEngineersDedicated*.log", SearchOption.TopDirectoryOnly)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Failed resolving latest Dedicated Server log for server {UniqueName}.", uniqueName);
            return null;
        }
    }

    private async Task<string> ReadDeltaAsync(string uniqueName, string filePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
            return string.Empty;

        long offset;
        lock (_sync)
        {
            if (!_offsets.TryGetValue(uniqueName, out var state) ||
                !string.Equals(state.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
            {
                state = new LogCursorState
                {
                    FilePath = filePath,
                    Offset = 0,
                };
                _offsets[uniqueName] = state;
            }

            offset = state.Offset;
        }

        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (offset > stream.Length)
            offset = 0;

        stream.Seek(offset, SeekOrigin.Begin);

        using var reader = new StreamReader(stream);
        var contents = await reader.ReadToEndAsync(cancellationToken);
        var newOffset = stream.Position;

        lock (_sync)
        {
            if (_offsets.TryGetValue(uniqueName, out var state) &&
                string.Equals(state.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
            {
                state.Offset = newOffset;
            }
        }

        return contents;
    }

    private static IEnumerable<string> ChunkText(string value, int chunkSize)
    {
        if (string.IsNullOrEmpty(value))
            yield break;

        for (var index = 0; index < value.Length; index += chunkSize)
            yield return value.Substring(index, Math.Min(chunkSize, value.Length - index));
    }

    private static string EscapeCodeBlock(string value)
    {
        return value.Replace("```", "``\u200B`", StringComparison.Ordinal);
    }

    private sealed class LogCursorState
    {
        public string FilePath { get; set; } = string.Empty;

        public long Offset { get; set; }
    }
}
