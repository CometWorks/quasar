using System.Text.Json;
using Quasar.Models;
using Quasar.Services.Backup;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Services.Analytics;
using Quasar.Services.Auth;

namespace Quasar.Services.Plugins;

/// <summary>Built-in consumers use the same operation boundary as plugins.</summary>
public static class QuasarCoreOperations
{
    public const string PluginId = "quasar.core";
    public sealed record AnalyticsRequest(long From, long To, string[] Servers, string[] Metrics, int MaxPoints = 1000);
    public sealed record BackupRequest(string? Name = null);
    public sealed record LogRequest(string UniqueName = "", string? Name = null);
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly QuasarOperationMetadata Read = new() { Effect = QuasarOperationEffect.Read };
    // Backup archives hold secrets and the Discord log is for admins only.
    private static readonly QuasarOperationMetadata ReadDeniedToAgents = Read with { AgentAccess = QuasarAgentAccess.Denied };

    public static void AddQuasarCoreOperations(this IServiceCollection services)
    {
        services.AddQuasarServerOperations();
        services.AddQuasarConfigOperations();
        services.AddQuasarPlayerOperations();
        services.AddQuasarBackupOperations();
        services.AddQuasarClusterOperations();
        services.AddQuasarRead<BackupRequest>(PluginId, "backup.read", [QuasarPolicyNames.CanManageSecurity],
            (caller, input, token) =>
            {
                var service = caller.Services.GetRequiredService<QuasarBackupService>();
                if (string.IsNullOrWhiteSpace(input.Name))
                {
                    var archive = service.CreateBackup(DateTimeOffset.Now);
                    return Task.FromResult(new QuasarRead(_ => Task.FromResult<Stream>(new MemoryStream(archive.Content, writable: false)),
                        "application/zip", archive.FileName, archive.Content.LongLength));
                }
                var path = service.ResolveBackupPath(input.Name) ?? throw new QuasarOperationException(404, "Backup not found.");
                return Task.FromResult(new QuasarRead(cancellation =>
                {
                    cancellation.ThrowIfCancellationRequested();
                    try { return Task.FromResult<Stream>(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, QuasarSdkLimits.TransferBufferBytes, FileOptions.Asynchronous)); }
                    catch (IOException) { throw new QuasarOperationException(404, "Backup not found."); }
                }, "application/zip", Path.GetFileName(path), EnableRanges: true));
            }, ReadDeniedToAgents with { Description = "Downloads a Quasar backup archive by name, or a fresh configuration backup when no name is given." });
        services.AddQuasarOperation<AnalyticsRequest, AnalyticsSeriesResponse>(PluginId, "analytics.series.read", [QuasarPolicyNames.CanView],
            (caller, input, token) =>
            {
                token.ThrowIfCancellationRequested();
                return Task.FromResult(caller.Services.GetRequiredService<AnalyticsSeriesService>().Build(
                    input.From, input.To, input.Servers ?? [], input.Metrics ?? [], input.MaxPoints));
            }, Read with { Description = "Returns analytics time series of the given servers and metrics between two Unix times in milliseconds, at most maxPoints per series." });
        services.AddQuasarRead<LogRequest>(PluginId, "logs.server.read", [QuasarPolicyNames.CanView],
            (caller, input, token) => Task.FromResult(ReadLog(ResolveDedicatedServerLogPath(input.UniqueName,
                caller.Services.GetRequiredService<DedicatedServerCatalog>(), input.Name))), Read with { Description = "Downloads a server's Dedicated Server log file, the newest one unless a file name is given." });
        services.AddQuasarRead<LogRequest>(PluginId, "logs.magnetar.read", [QuasarPolicyNames.CanView],
            (caller, input, token) => Task.FromResult(ReadLog(ResolveMagnetarInfoLogPath(input.UniqueName,
                caller.Services.GetRequiredService<DedicatedServerCatalog>(), input.Name))), Read with { Description = "Downloads a server's Magnetar log file, the newest one unless a file name is given." });
        services.AddQuasarRead<LogRequest>(PluginId, "logs.discord.read", [QuasarPolicyNames.CanManageDiscord],
            (caller, input, token) => Task.FromResult(ReadLog(QuasarLoggingConfigurator.ResolveDiscordLogPath(
                caller.Services.GetRequiredService<WebServiceOptions>()))), ReadDeniedToAgents with { Description = "Downloads Quasar's Discord bot log." });
    }

    public static void MapQuasarCoreOperations(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapQuasarOperationRead("/api/backup/download", PluginId, "backup.read");
        endpoints.MapQuasarOperation("/api/backup/download/{name}", ["GET"], PluginId, "backup.read", http =>
            ValueTask.FromResult(JsonSerializer.SerializeToElement(new BackupRequest(http.Request.RouteValues["name"]?.ToString()))));
        endpoints.MapQuasarOperation("/api/analytics/series", ["GET"], PluginId, "analytics.series.read", http =>
        {
            var query = http.Request.Query;
            _ = long.TryParse(query["from"], out var from);
            _ = long.TryParse(query["to"], out var to);
            _ = int.TryParse(query["maxPoints"], out var maxPoints);
            return ValueTask.FromResult(JsonSerializer.SerializeToElement(new AnalyticsRequest(from, to,
                query["servers"].Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!).ToArray(),
                query["metrics"].Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!).ToArray(), maxPoints)));
        });
        foreach (var kind in new[] { "server", "magnetar" })
            endpoints.MapQuasarOperation("/api/servers/{uniqueName}/logs/" + kind + "/download", ["GET"], PluginId, "logs." + kind + ".read", http =>
                ValueTask.FromResult(JsonSerializer.SerializeToElement(new LogRequest(http.Request.RouteValues["uniqueName"]!.ToString()!, http.Request.Query["name"].FirstOrDefault()))));
        endpoints.MapQuasarOperationRead("/api/discord/log/download", PluginId, "logs.discord.read");
    }

    internal static DedicatedServerDefinition RequireServer(IServiceProvider services, string uniqueName) =>
        services.GetRequiredService<DedicatedServerCatalog>().GetServer(uniqueName?.Trim() ?? string.Empty)
        ?? throw new QuasarOperationException(404, $"Server '{uniqueName}' not found.");

    internal static bool JsonEquals(JsonElement left, object? right) =>
        System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(left.GetRawText()),
            JsonSerializer.SerializeToNode(right, Json));

    private static QuasarRead ReadLog(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) throw new QuasarOperationException(404, "Log file not found.");
        return new(token =>
        {
            token.ThrowIfCancellationRequested();
            try { return Task.FromResult<Stream>(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { throw new QuasarOperationException(404, "Log file not found."); }
        }, "text/plain; charset=utf-8", Path.GetFileName(path));
    }

    internal static string? ResolveDedicatedServerLogPath(string uniqueName, DedicatedServerCatalog catalog, string? fileName)
    {
        var server = catalog.GetServer(uniqueName);
        if (server is null)
            return null;

        var appDataPath = DedicatedServerPathResolver.Resolve(server).DedicatedServerAppDataPath;

        if (!Directory.Exists(appDataPath))
            return null;

        return ResolveLogPath(appDataPath, "SpaceEngineersDedicated*.log", fileName);
    }

    internal static string? ResolveMagnetarInfoLogPath(string uniqueName, DedicatedServerCatalog catalog, string? fileName)
    {
        var server = catalog.GetServer(uniqueName);
        if (server is null)
            return null;

        var appDataPath = DedicatedServerPathResolver.Resolve(server).MagnetarAppDataPath;

        if (!Directory.Exists(appDataPath))
            return null;

        return ResolveLogPath(appDataPath, "info*.log", fileName);
    }

    private static string? ResolveLogPath(string directory, string searchPattern, string? fileName)
    {
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            var trimmedFileName = fileName.Trim();
            if (!string.Equals(trimmedFileName, Path.GetFileName(trimmedFileName), StringComparison.Ordinal) ||
                trimmedFileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                return null;
            }

            return Directory.EnumerateFiles(directory, searchPattern, SearchOption.TopDirectoryOnly)
                .FirstOrDefault(path => string.Equals(
                    Path.GetFileName(path),
                    trimmedFileName,
                    StringComparison.OrdinalIgnoreCase));
        }

        return Directory.EnumerateFiles(directory, searchPattern, SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ThenByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

}
