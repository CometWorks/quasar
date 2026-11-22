using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Quasar.Client.Fleet;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Services.Auth;
using static Quasar.Services.Plugins.QuasarCoreOperations;

namespace Quasar.Services.Plugins;

public static class QuasarConversionDashboardOperations
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string[] Policies = [QuasarPolicyNames.ClusterManage, QuasarPolicyNames.CanEditServers, QuasarPolicyNames.CanEditConfigs];
    private static readonly QuasarOperationMetadata Read = new() { AgentAccess = QuasarAgentAccess.Denied, RequireDefaultPolicies = true, Effect = QuasarOperationEffect.Read };

    public static void AddQuasarConversionDashboardOperations(this IServiceCollection services)
    {
        services.AddSingleton<DashboardConversionJobs>();
        services.AddQuasarOperation<ConversionSource, ConversionPageState>(PluginId, "conversion.page", Policies,
            async (caller, input, _) =>
            {
                await Authorize(caller, input);
                var clusters = caller.Services.GetRequiredService<ClusterCatalog>();
                var profiles = caller.Services.GetRequiredService<QuasarConfigProfileCatalog>();
                var conversions = caller.Services.GetRequiredService<ClusterConversionService>();
                var cluster = input.Cluster is null ? null : clusters.GetCluster(input.Cluster);
                var draft = new ConversionDraft("", true, cluster is null ? "" : cluster.UniqueName + "-standalone", cluster?.DisplayName ?? "", 27017, cluster?.ConfigProfileId ?? "");
                ConversionProgress? progress = null;
                string? reviewError = null;
                string[] warnings = [];
                if (input.Id.HasValue)
                {
                    var saved = Saved(caller, input);
                    if (saved is null) throw new QuasarOperationException(404, "Conversion was not found. Open the source page to start a new conversion.");
                    draft = saved.Value.Draft;
                    progress = Progress(caller, input);
                }
                else if (input.Server is not null)
                {
                    try { warnings = conversions.ReviewServer(input.Server).Warnings; }
                    catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException) { reviewError = error.Message; }
                }
                return new(clusters.GetClusters().Where(c => caller.User.CanQueryCluster(c.UniqueName)
                    && (ClusterConversionService.IsEmpty(c) || input.Id.HasValue && c.UniqueName == draft.Cluster))
                    .Select(c => new ConversionDestination(c.UniqueName, c.DisplayName)).ToArray(),
                    profiles.GetProfiles().Select(p => new ConversionDestination(p.ConfigProfileId, p.Name)).ToArray(), draft, warnings, reviewError, progress);
            }, Read with { Description = "Reviews a cluster conversion or loads its original destination choices." });
        services.AddQuasarOperation<ConversionSource, ConversionProgress?>(PluginId, "conversion.progress", Policies,
            async (caller, source, _) =>
            {
                await Authorize(caller, source);
                if (Saved(caller, source) is null) return null;
                return Progress(caller, source);
            }, Read with { Description = "Reads the progress of a conversion for a permitted source." });
        services.AddQuasarOperation<ConversionSubmission, ConversionProgress?>(PluginId, "conversion.start", Policies,
            async (caller, input, cancellationToken) =>
            {
                var source = input.Source;
                await Authorize(caller, source);
                if (source.Id is not { } id || id == Guid.Empty || input.Attempt == Guid.Empty)
                    throw new QuasarOperationException(400, "Conversion and attempt IDs are required.");
                var saved = Saved(caller, source);
                var draft = saved?.Draft ?? input.Draft;
                var cluster = source.Cluster ?? (draft.CreateCluster ? "cluster-" + id.ToString("N") : draft.Cluster);
                CheckCluster(caller.User, cluster);
                var conversions = caller.Services.GetRequiredService<ClusterConversionService>();
                var jobs = caller.Services.GetRequiredService<DashboardConversionJobs>();
                var actor = caller.User.Identity?.Name ?? "web-ui";
                var key = "conversion-" + input.Attempt.ToString("N");
                var lifetime = caller.Services.GetRequiredService<IHostApplicationLifetime>();
                var token = lifetime.ApplicationStopping;
                Task<ClusterOperation> job;
                if (source.Server is { } server)
                {
                    var forward = saved is { } s ? s.Request.Deserialize<ServerToClusterRequest>(Json)!
                        : ServerToClusterRequest.ForServer(id, server, draft.CreateCluster);
                    job = jobs.Start(id, source, () => conversions.ToClusterAsync(cluster, forward, key, actor, token));
                }
                else
                {
                    var reverse = saved is { } s ? s.Request.Deserialize<ClusterToServerRequest>(Json)!
                        : new ClusterToServerRequest(id, draft.ServerId, draft.Name, draft.Port, draft.Profile,
                            caller.Services.GetRequiredService<ClusterCatalog>().GetCluster(cluster)?.GetLifecycleId() ?? "");
                    job = jobs.Start(id, source, () => conversions.ToServerAsync(cluster, reverse, key, actor, token));
                }
                // A world copy can outlast both the SDK deadline and a browser session. Return once the
                // service has persisted its request, so the URL is sufficient to recover after a reload.
                while (Saved(caller, source) is null && !job.IsCompleted) await Task.Delay(50, cancellationToken);
                if (job.IsCompleted)
                {
                    try
                    {
                        var result = await job;
                        if (result.Error is { } error) return new("Failed", null, error.Message, false);
                    }
                    catch (ClusterOperationConflictException error) { throw new QuasarOperationException(409, error.Message); }
                }
                return Progress(caller, source);
            }, Read with { Effect = QuasarOperationEffect.Change, Description = "Starts or resumes the source-bound server or cluster conversion." });
        services.AddQuasarOperation<EmptyRequest, bool>(PluginId, "cluster.notice", [QuasarPolicyNames.CanView],
            (caller, _, _) => Task.FromResult(caller.User.Identity?.IsAuthenticated == true
                && !caller.Services.GetRequiredService<ClusterBetaNoticeCatalog>().IsDismissed(caller.User)),
            Read with { Description = "Checks whether the viewer has acknowledged the cluster beta notice." });
        services.AddQuasarOperation<EmptyRequest, bool>(PluginId, "cluster.notice.dismiss", [QuasarPolicyNames.CanView],
            async (caller, _, token) =>
            {
                await caller.Services.GetRequiredService<ClusterBetaNoticeCatalog>().DismissAsync(caller.User, token);
                return true;
            }, Read with { Effect = QuasarOperationEffect.Change, Description = "Dismisses the cluster beta notice for the current viewer." });
    }

    private static async Task Authorize(QuasarOperationContext caller, ConversionSource input)
    {
        if ((input.Server is null) == (input.Cluster is null) || string.IsNullOrWhiteSpace(input.Server ?? input.Cluster))
            throw new QuasarOperationException(400, "Select a source server or cluster.");
        var auth = caller.Services.GetRequiredService<IAuthorizationService>();
        foreach (var policy in Policies)
            if (!(await auth.AuthorizeAsync(caller.User, null, policy)).Succeeded)
                throw new QuasarOperationException(403, "You do not have permission to convert servers.");
        if (input.Cluster is { } cluster) CheckCluster(caller.User, cluster);
    }

    private static void CheckCluster(ClaimsPrincipal user, string id)
    {
        if (string.IsNullOrWhiteSpace(id) || !user.CanQueryCluster(id)) throw new QuasarOperationException(403, "Cluster access denied.");
    }

    private static (ConversionDraft Draft, JsonElement Request)? Saved(QuasarOperationContext caller, ConversionSource source)
    {
        if (source.Id is not { } id) throw new QuasarOperationException(400, "Conversion ID is required.");
        var conversions = caller.Services.GetRequiredService<ClusterConversionService>();
        (string Cluster, string Direction, JsonElement Request) saved;
        try { saved = conversions.GetRequest(id); }
        catch (Exception error) when (error is DirectoryNotFoundException or FileNotFoundException) { return null; }
        CheckCluster(caller.User, saved.Cluster);
        if (source.Server is { } server && saved.Direction == "to-cluster" && saved.Request.Deserialize<ServerToClusterRequest>(Json) is { } forward && forward.Server == server)
            return (new(saved.Cluster, forward.CreateCluster, "", "", 27017, ""), saved.Request);
        if (source.Cluster == saved.Cluster && saved.Direction == "to-server" && saved.Request.Deserialize<ClusterToServerRequest>(Json) is { } reverse)
            return (new(saved.Cluster, false, reverse.UniqueName, reverse.DisplayName, reverse.Port, reverse.ConfigProfileId), saved.Request);
        throw new QuasarOperationException(404, "Conversion was not found for this source.");
    }

    private static ConversionProgress Progress(QuasarOperationContext caller, ConversionSource source)
    {
        var id = source.Id!.Value;
        var state = caller.Services.GetRequiredService<ClusterConversionService>().GetStatus(id);
        var running = caller.Services.GetRequiredService<DashboardConversionJobs>().IsRunning(id);
        return new(state?.Phase ?? "Preparing conversion", state?.Destination, state?.Error, running);
    }
}

public sealed class DashboardConversionJobs(ILogger<DashboardConversionJobs> logger)
{
    private readonly Dictionary<Guid, (ConversionSource Source, Task<ClusterOperation> Work)> _active = new();
    private readonly object _gate = new();

    public bool IsRunning(Guid id) { lock (_gate) return _active.ContainsKey(id); }

    public Task<ClusterOperation> Start(Guid id, ConversionSource source, Func<Task<ClusterOperation>> execute)
    {
        lock (_gate)
        {
            if (_active.TryGetValue(id, out var active))
            {
                if (active.Source != source) throw new QuasarOperationException(409, "Conversion ID is already in use.");
                return active.Work;
            }
            var job = execute();
            _active.Add(id, (source, job));
            _ = ObserveAsync(id, job);
            return job;
        }
    }

    private async Task ObserveAsync(Guid id, Task<ClusterOperation> job)
    {
        try { await job; }
        catch (Exception error) { logger.LogWarning(error, "Conversion {ConversionId} stopped", id); }
        finally { lock (_gate) _active.Remove(id); }
    }
}
