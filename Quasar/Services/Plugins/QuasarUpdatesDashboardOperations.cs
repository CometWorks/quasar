using System.Text;
using System.Text.Json;
using Magnetar.Protocol.Runtime;
using Quasar.Client.Administration;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Services.Auth;
using Quasar.Services.Updates;

namespace Quasar.Services.Plugins;

internal static class QuasarUpdatesDashboardOperations
{
    private sealed record VersionInput(string Version, bool Retry = false);
    private sealed record ToggleInput(bool Enabled);
    private sealed record ModeInput(DedicatedServerLaunchUpdateMode Mode);
    private sealed record TokenInput(string Token);

    internal static void AddQuasarUpdatesDashboard(this IServiceCollection services)
    {
        const string plugin = QuasarCoreOperations.PluginId;
        string[] manage = [QuasarPolicyNames.CanManageSecurity];
        var metadata = new QuasarOperationMetadata { RequireDefaultPolicies = true, AgentAccess = QuasarAgentAccess.Denied };
        var read = metadata with { Effect = QuasarOperationEffect.Read };
        services.AddQuasarOperation<QuasarCoreOperations.EmptyRequest, UpdatesDashboard>(plugin, "updates.dashboard", manage,
            (caller, _, _) => Task.FromResult(Snapshot(caller)), read);
        AddCommand("updates.check", (provider, token) => provider.GetRequiredService<QuasarUpdateService>().CheckNowAsync(token));
        AddCommand("updates.stage", (provider, token) => provider.GetRequiredService<QuasarUpdateService>().StageWebUpdateAsync(token));
        AddCommand("updates.activate", (provider, token) => provider.GetRequiredService<QuasarUpdateService>().ActivateStagedWebUpdateAsync(token));
        AddCommand("updates.bootstrap.activate", (provider, token) => provider.GetRequiredService<QuasarUpdateService>().RequestBootstrapUpdateActivationAsync(token));
        AddCommand("updates.clusters.check", (provider, token) => provider.GetRequiredService<ClusterReleaseMonitor>().CheckNowAsync(token));
        AddCommand("updates.magnetar.check", (provider, token) => provider.GetRequiredService<ManagedRuntimeWarmupService>().CheckMagnetarNowAsync(token));
        AddCommand("updates.dedicated.check", (provider, token) => provider.GetRequiredService<ManagedRuntimeWarmupService>().CheckDedicatedServerNowAsync(token));
        AddCommand("updates.token.clear", (provider, token) => provider.GetRequiredService<GitHubUpdateCredentialsCatalog>().ClearAsync(token));
        services.AddQuasarOperation<VersionInput, string>(plugin, "updates.resolve", manage,
            (caller, input, token) => Updates(caller).ResolveUpdateVersionAsync(input.Version, token), read);
        AddChange<VersionInput>("updates.install", (caller, input, token) => Updates(caller).UpdateAsync(input.Version, input.Retry, token));
        AddChange<VersionInput>("updates.select", (caller, input, token) => Updates(caller).SelectWebReleaseAsync(input.Version, token));
        AddChange<ToggleInput>("updates.prerelease", async (caller, input, token) =>
        {
            await Updates(caller).SetIncludePrereleaseAsync(input.Enabled, token);
            await Updates(caller).CheckNowAsync(token);
        });
        AddChange<ToggleInput>("updates.auto.apply", (caller, input, token) => Updates(caller).SetAutoApplyUpdatesAsync(input.Enabled, token));
        AddChange<ToggleInput>("updates.auto.stage", (caller, input, token) => Updates(caller).SetAutoStageWebUpdatesAsync(input.Enabled, token));
        AddChange<ModeInput>("updates.runtime.mode", (caller, input, token) => Enum.IsDefined(input.Mode)
            ? caller.Services.GetRequiredService<ManagedDedicatedServerRuntimeResolver>().SetDedicatedServerLaunchUpdateModeAsync(input.Mode, token)
            : throw new QuasarOperationException(400, "Invalid launch update mode."));
        AddChange<TokenInput>("updates.token.save", (caller, input, token) =>
            caller.Services.GetRequiredService<GitHubUpdateCredentialsCatalog>().SaveAsync(new() { Token = input.Token }, token));
        services.AddQuasarRead<QuasarCoreOperations.EmptyRequest>(plugin, "updates.conflict.documents", manage,
            async (caller, _, token) =>
            {
                if (!Updates(caller).GetSnapshot().AppSettingsConflict) throw new QuasarOperationException(409, "No appsettings conflict.");
                var documents = await Updates(caller).ReadAppSettingsConflictDocumentsAsync(token);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(new ConflictDocuments(documents.Current, documents.Incoming), QuasarCoreOperations.Json);
                return new QuasarRead(_ => Task.FromResult<Stream>(new MemoryStream(bytes, writable: false)), "application/json", Length: bytes.Length);
            }, read);
        services.AddQuasarUpload<QuasarCoreOperations.EmptyRequest, bool>(plugin, "updates.conflict.resolve", manage, 32 * 1024 * 1024,
            (caller, _, stream, _) => Task.FromResult(new QuasarUpload<bool>(async token =>
            {
                if (!Updates(caller).GetSnapshot().AppSettingsConflict) throw new QuasarOperationException(409, "No appsettings conflict.");
                using var reader = new StreamReader(stream, new UTF8Encoding(false, true), leaveOpen: true);
                var document = await reader.ReadToEndAsync(token);
                await caller.EnsureAuthorizedAsync(token);
                await Updates(caller).ResolveAppSettingsConflictAsync(document, token);
                return true;
            })), metadata);

        void AddCommand(string id, Func<IServiceProvider, CancellationToken, Task> run) =>
            AddChange<QuasarCoreOperations.EmptyRequest>(id, (caller, _, token) => run(caller.Services, token));

        void AddChange<T>(string id, Func<QuasarOperationContext, T, CancellationToken, Task> run) =>
            services.AddQuasarOperation<T, bool>(plugin, id, manage,
                async (caller, input, token) => { await run(caller, input, token); return true; },
                metadata with { Deadline = QuasarOperationMetadata.MaximumDeadline });
    }

    private static QuasarUpdateService Updates(QuasarOperationContext caller) => caller.Services.GetRequiredService<QuasarUpdateService>();

    private static UpdatesDashboard Snapshot(QuasarOperationContext caller)
    {
        var provider = caller.Services;
        var update = Updates(caller).GetSnapshot();
        var options = provider.GetRequiredService<QuasarUpdateOptions>();
        var web = provider.GetRequiredService<WebServiceOptions>();
        var release = provider.GetRequiredService<ClusterReleaseMonitor>().GetSnapshot();
        var clusters = OperatingSystem.IsLinux() ? provider.GetRequiredService<ClusterCatalog>().GetClusters()
            .Where(cluster => cluster.PackageSelection is not null && caller.User.CanQueryCluster(cluster.UniqueName))
            .Select(cluster => new UpdateCluster(cluster.UniqueName, cluster.DisplayName, cluster.PackageSelection!.Version,
                ClusterReleaseMonitor.IsUpdateAvailable(cluster, release.Release))).ToArray() : [];
        var restart = update.RestartCapability;
        return new(update.Status.ToString(), update.Enabled, update.CurrentVersion, update.CurrentBootstrapVersion, update.Message,
            update.LastCheckedUtc, update.SelectedWebVersion, update.Web is null ? null : Release(update.Web), update.WebReleases.Select(Release).ToArray(),
            update.Bootstrap is null ? null : Release(update.Bootstrap),
            update.Operation is {} op ? new(op.Id, op.Version, op.Stage.ToString(), op.Message, op.UpdatedAtUtc, op.PackageDirectory) : null,
            new(restart.CanRestart, restart.Supervisor, restart.Detail, restart.Repair), update.AppSettingsConflictPath, update.AppSettingsConflict,
            update.GitHubTokenWarning, update.GitHubTokenConfigured, update.GitHubTokenSaved, update.GitHubTokenExpired, update.GitHubTokenExpiringSoon,
            update.GitHubTokenVariableName, update.GitHubTokenExpiresAtUtc, options.IncludePrerelease, options.AutoStageWebUpdates,
            new(options.Owner, options.Repository, options.CheckInterval, options.AutoApplyUpdates, options.PackageAssetName, options.WebAssetName,
                web.BootstrapVersion, !string.IsNullOrWhiteSpace(web.LauncherToken), web.BootstrapUpdateUnavailableReason,
                Path.GetFileName(MagnetarPaths.GetQuasarGitHubUpdateCredentialsPath()), ManagedRuntimeWarmupService.MagnetarUpdateCheckPeriod.TotalMinutes),
            provider.GetRequiredService<ManagedDedicatedServerRuntimeResolver>().DedicatedServerLaunchUpdateMode,
            provider.GetRequiredService<ManagedRuntimeWarmupService>().GetSnapshot(), new(release.Release?.Version, release.LastCheckedUtc, release.Error, clusters));
    }

    private static UpdateRelease Release(QuasarUpdateCandidate release) =>
        new(release.Version, release.AssetName, release.IsCurrent, release.IsNewer, release.IsPrerelease, release.IsStaged);
}
