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
    public void Dispose() => Zip?.Dispose();
}

internal static class QuasarServerImportOperations
{
    internal sealed record Source(string Path);
    private static readonly string[] Edit = [QuasarPolicyNames.CanEditServers];
    private static readonly QuasarOperationMetadata Change = new() { RequireDefaultPolicies = true, AgentAccess = QuasarAgentAccess.Denied, Effect = QuasarOperationEffect.Change };
    private static readonly QuasarOperationMetadata Read = Change with { Effect = QuasarOperationEffect.Read };
    public static void AddQuasarServerImportOperations(this IServiceCollection services)
    {
        services.AddSingleton<DashboardServerImportStaging>();
        services.AddQuasarUpload<ServerImportTarget, bool>(PluginId, "server-import.zip", Edit, WorldSaveFiles.MaxUploadBytes,
            (caller, target, staged, _) => Task.FromResult(new QuasarUpload<bool>(token => Inspect(caller, target, staged, null, token))), Change with { Description = "Stages and inspects an uploaded server archive." });
        services.AddQuasarUpload<ServerImportTarget, bool>(PluginId, "server-import.folder", Edit, QuasarSdkLimits.MaximumJsonBytes,
            (caller, target, staged, _) => Task.FromResult(new QuasarUpload<bool>(async token =>
            {
                var source = await JsonSerializer.DeserializeAsync<Source>(staged, Json, token);
                if (string.IsNullOrWhiteSpace(source?.Path)) throw new QuasarOperationException(400, "Select a server folder.");
                return await Inspect(caller, target, null, source.Path, token);
            })), Change with { Description = "Inspects a selected server folder for import." });
        services.AddQuasarOperation<ServerImportTarget, WorldTransferProgress?>(PluginId, "server-import.progress", Edit,
            (caller, target, _) => Task.FromResult(Staging(caller).Status(caller.User, target)), Read with { Description = "Returns progress for the caller's staged server import." });
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
        { Staging(caller).Discard(caller.User, target); return Task.FromResult(true); }, Change with { Description = "Discards the caller's staged server import." });
    }

    private static DashboardServerImportStaging Staging(QuasarOperationContext caller) => caller.Services.GetRequiredService<DashboardServerImportStaging>();

    private static async Task<bool> Inspect(QuasarOperationContext caller, ServerImportTarget target, Stream? zip, string? path, CancellationToken token)
    {
        await caller.EnsureAuthorizedAsync(token);
        if (!Enum.IsDefined(target.Kind)) throw new QuasarOperationException(400, "Unknown server format.");
        var service = caller.Services.GetRequiredService<ExistingServerImportService>();
        var registry = Staging(caller);
        var entry = registry.Begin(caller.User, target);
        var complete = false;
        try
        {
            if (zip is not null)
                entry.Stage.Zip = await service.StageZipAsync(target.Kind, zip, Path.Combine(Path.GetTempPath(), "quasar-server-imports"),
                    new Progress<WorldImportProgress>(p => entry.Progress = new(p.Phase, p.Bytes, p.CompletedFiles, p.TotalFiles)), token);
            entry.Stage.Analysis = entry.Stage.Zip?.Analysis ?? await Task.Run(() => service.Analyze(target.Kind, path!), token);
            complete = true;
            return true;
        }
        finally { registry.Release(entry, !complete); }
    }
}
