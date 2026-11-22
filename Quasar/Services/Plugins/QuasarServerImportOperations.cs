using System.Text.Json;
using Quasar.Client.Fleet;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Services.Auth;
using static Quasar.Services.Plugins.QuasarCoreOperations;

namespace Quasar.Services.Plugins;

internal sealed class DashboardServerImportStaging() : DashboardImportStaging<ServerImportTarget, ServerImportStage>(target => target.Id);
internal sealed class ServerImportStage : IDisposable
{
    internal StagedServerImport? Zip;
    internal ExistingServerImportAnalysis? Analysis;
    internal DashboardZipRanges? Ranges;
    public void Dispose() { Ranges?.Dispose(); Zip?.Dispose(); }
}

internal static class QuasarServerImportOperations
{
    internal sealed record ZipInput(ServerImportTarget Target, long Size, long DirectoryOffset);
    internal sealed record ZipRangeInput(ServerImportTarget Target, long Sequence);
    internal sealed record Source(string Path);
    private static readonly string[] Edit = [QuasarPolicyNames.CanEditServers];
    private static readonly QuasarOperationMetadata Change = new() { RequireDefaultPolicies = true, AgentAccess = QuasarAgentAccess.Denied, Effect = QuasarOperationEffect.Change };
    private static readonly QuasarOperationMetadata Read = Change with { Effect = QuasarOperationEffect.Read };
    public static void AddQuasarServerImportOperations(this IServiceCollection services)
    {
        services.AddSingleton<DashboardServerImportStaging>();
        services.AddQuasarOperation<ZipInput, ZipSlice>(PluginId, "server-import.zip.start", Edit, StartZip,
            Change with { Description = "Starts the worker ZIP review and requests its first byte range." });
        services.AddQuasarUpload<ZipRangeInput, ZipSlice>(PluginId, "server-import.zip.range", Edit, WorldSaveFiles.MaxUploadBytes,
            (caller, input, body, _) => Task.FromResult(new QuasarUpload<ZipSlice>(token =>
            {
                var ranges = Staging(caller).GetStage(caller.User, input.Target)?.Ranges
                    ?? throw new QuasarOperationException(404, "The ZIP import has expired. Select the file again.");
                return ranges.SupplyAsync(input.Sequence, body, token);
            })), Change with { Description = "Supplies the selected source file range requested by the worker." });
        services.AddQuasarUpload<ServerImportTarget, bool>(PluginId, "server-import.folder", Edit, QuasarSdkLimits.MaximumJsonBytes,
            (caller, target, staged, _) => Task.FromResult(new QuasarUpload<bool>(async token =>
            {
                var source = await JsonSerializer.DeserializeAsync<Source>(staged, Json, token);
                if (string.IsNullOrWhiteSpace(source?.Path)) throw new QuasarOperationException(400, "Select a server folder.");
                return await InspectFolder(caller, target, source.Path, token);
            })), Change with { Description = "Inspects a selected server folder for import." });
        services.AddQuasarOperation<ServerImportTarget, WorldTransferProgress?>(PluginId, "server-import.progress", Edit,
            (caller, target, _) =>
        {
            var staging = Staging(caller);
            staging.GetStage(caller.User, target)?.Ranges?.KeepAlive();
            return Task.FromResult(staging.Status(caller.User, target));
        }, Read with { Description = "Returns progress for the caller's staged server import." });
        services.AddQuasarRead<ServerImportTarget>(PluginId, "server-import.review", Edit, (caller, target, _) =>
        {
            var registry = Staging(caller);
            var entry = registry.Claim(caller.User, target);
            try
            {
                var analysis = entry.Stage.Analysis!;
                var review = new ServerImportReview(analysis.ServerName, analysis.ConfigPath, analysis.Warnings,
                    analysis.Worlds.Select(w => new ImportWorld(w.Name, w.Path, w.IsConfiguredWorld, w.SessionSettingCount, w.ModCount)).ToArray(),
                    analysis.RootSettingCount, analysis.SessionSettingCount, analysis.AccessEntryCount, analysis.HasPasswordHash);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(review, Json);
                return Task.FromResult(new QuasarRead(_ => Task.FromResult<Stream>(new MemoryStream(bytes, false)), "application/json", Length: bytes.Length));
            }
            finally { registry.Release(entry, false); }
        }, Read with { Description = "Returns detected server settings and world choices for review." });
        services.AddQuasarUpload<ServerImportTarget, ImportedServer>(PluginId, "server-import.commit", Edit, QuasarSdkLimits.MaximumJsonBytes,
            (caller, target, staged, _) => Task.FromResult(new QuasarUpload<ImportedServer>(async token =>
            {
                var input = await JsonSerializer.DeserializeAsync<ImportServerSelection>(staged, Json, token) ?? throw new QuasarOperationException(400, "Missing import settings.");
                await caller.EnsureAuthorizedAsync(token);
                var registry = Staging(caller);
                var entry = registry.Claim(caller.User, target);
                try
                {
                    var source = entry.Stage.Analysis!;
                    var importer = caller.Services.GetRequiredService<ExistingServerImportService>();
                    if (!Enum.IsDefined(input.TransferMode) || input.Worlds is not { Length: > 0 } ||
                        input.Worlds.Except(source.Worlds.Select(w => w.Path), StringComparer.Ordinal).Any() || !input.Worlds.Contains(input.PrimaryWorld, StringComparer.Ordinal))
                        throw new QuasarOperationException(400, "Select the primary world from the reviewed source.");
                    var request = new ExistingServerImportRequest(target.Kind, source.SourcePath, input.TransferMode, input.DisplayName, input.UniqueName,
                        input.Worlds, input.PrimaryWorld, input.Sections, input.Stopped);
                    var result = entry.Stage.Zip is {} archive ? await importer.ImportArchiveAsync(archive, request, token) : await importer.ImportAsync(request, token);
                    return new(result.Server.UniqueName, result.Server.DisplayName, result.Server.WorldTemplateId, result.WorldTemplates.Count, result.Warnings);
                }
                finally { registry.Release(entry, true); }
            })), Change with { Description = "Imports the reviewed worlds and creates a managed server." });
        services.AddQuasarOperation<ServerImportTarget, bool>(PluginId, "server-import.discard", Edit, (caller, target, _) =>
        {
            var staging = Staging(caller);
            staging.GetStage(caller.User, target)?.Ranges?.Dispose();
            staging.Discard(caller.User, target);
            return Task.FromResult(true);
        }, Change with { Description = "Discards the caller's staged server import." });
    }

    private static DashboardServerImportStaging Staging(QuasarOperationContext caller) => caller.Services.GetRequiredService<DashboardServerImportStaging>();

    private static async Task<ZipSlice> StartZip(QuasarOperationContext caller, ZipInput input, CancellationToken token)
    {
        await caller.EnsureAuthorizedAsync(token);
        if (!Enum.IsDefined(input.Target.Kind) || input.Size is <= 0 or > 9_007_199_254_740_991L ||
            input.DirectoryOffset < 0 || input.DirectoryOffset > input.Size)
            throw new QuasarOperationException(400, "Invalid source archive.");
        var registry = Staging(caller);
        var scopeFactory = caller.Services.GetRequiredService<IServiceScopeFactory>();
        var entry = registry.Begin(caller.User, input.Target);
        var ranges = entry.Stage.Ranges = new DashboardZipRanges();
        return await ranges.Start(input.Size, input.DirectoryOffset, async (archive, ct) =>
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<ExistingServerImportService>();
            entry.Stage.Zip = await service.StageZipAsync(input.Target.Kind, archive, Path.Combine(Path.GetTempPath(), "quasar-server-imports"),
                new Progress<WorldImportProgress>(p => entry.Progress = new(p.Phase, p.Bytes, p.CompletedFiles, p.TotalFiles)), ct);
            entry.Stage.Analysis = entry.Stage.Zip.Analysis;
        }, failed => registry.Release(entry, failed), token);
    }

    private static async Task<bool> InspectFolder(QuasarOperationContext caller, ServerImportTarget target, string path, CancellationToken token)
    {
        await caller.EnsureAuthorizedAsync(token);
        if (!Enum.IsDefined(target.Kind)) throw new QuasarOperationException(400, "Unknown server format.");
        var service = caller.Services.GetRequiredService<ExistingServerImportService>();
        var registry = Staging(caller);
        var entry = registry.Begin(caller.User, target);
        var complete = false;
        try
        {
            entry.Stage.Analysis = await Task.Run(() => service.Analyze(target.Kind, path), token);
            complete = true;
            return true;
        }
        finally { registry.Release(entry, !complete); }
    }
}
