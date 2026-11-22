using Microsoft.AspNetCore.Authorization;
using Quasar.Client.Fleet;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Services.Auth;
using static Quasar.Services.Plugins.QuasarCoreOperations;

namespace Quasar.Services.Plugins;

public static class QuasarClusterSetupDashboardOperations
{
    private static readonly QuasarOperationMetadata Read = new()
    { Effect = QuasarOperationEffect.Read, AgentAccess = QuasarAgentAccess.Denied, RequireDefaultPolicies = true };
    private static readonly string[] Manage = [QuasarPolicyNames.ClusterManage];
    private static readonly string[] Deploy = [QuasarPolicyNames.ClusterManage, QuasarPolicyNames.CanEditConfigs];

    public static void AddQuasarClusterSetupDashboardOperations(this IServiceCollection services)
    {
        services.AddSingleton<DashboardSetupJobs>();
        services.AddQuasarOperation<SetupLookup, SetupSnapshot>(PluginId, "cluster.setup.page", Manage, async (caller, input, _) =>
        {
            var services = caller.Services;
            var local = services.GetRequiredService<ClusterLocalHost>();
            local.Request();
            var tunnels = services.GetRequiredService<ClusterHostTunnels>();
            var machines = services.GetRequiredService<ClusterHostCatalog>().GetAll().Select(host =>
                new SetupMachine(new(host.Id, host.Name, host.Address), tunnels.IsConnected(host.Id))).ToArray();
            var worlds = services.GetRequiredService<QuasarWorldTemplateCatalog>().GetTemplates()
                .Select(w => new ConversionDestination(w.WorldTemplateId, w.Name)).ToArray();
            var profiles = services.GetRequiredService<QuasarConfigProfileCatalog>().GetProfiles()
                .Select(p => new SetupProfile(p.ConfigProfileId, p.Name, QuasarDefaultPlugins.Pending(p).Select(plugin => plugin.DisplayName).ToArray())).ToArray();
            ClusterSetupStatus? status = null;
            ClusterOperation? operation = null;
            DashboardSetupJobs.Job? active = null;
            var registered = false;
            if (!string.IsNullOrWhiteSpace(input.Id))
            {
                CheckCluster(caller, input.Id);
                if (!await Allowed(caller, QuasarPolicyNames.CanEditConfigs))
                    throw new QuasarOperationException(403, "Configuration permission is required to resume setup.");
                status = services.GetRequiredService<ClusterSetupService>().GetStatus(input.Id);
                active = services.GetRequiredService<DashboardSetupJobs>().Get(input.Id);
                registered = services.GetRequiredService<ClusterCatalog>().GetCluster(input.Id) is not null;
                if (input.Attempt is { } attempt)
                    operation = services.GetRequiredService<ClusterOperationStore>().Find(input.Id, "cluster.setup", attempt.ToString("N"));
            }
            return new(machines, worlds, profiles, local.Error, await Allowed(caller, QuasarPolicyNames.CanManageSecurity),
                status, active?.Request, registered, active?.Progress?.Message, active?.Progress?.Percent, operation?.Error?.Message);
        }, Read with { Description = "Reads the guided setup choices, machine connectivity and progress of the permitted cluster." });
        services.AddQuasarOperation<SetupSubmission, bool>(PluginId, "cluster.setup.deploy", Deploy, async (caller, input, token) =>
        {
            if (input.Attempt == Guid.Empty || input.Request is null) throw new QuasarOperationException(400, "Setup request and attempt ID are required.");
            var request = input.Request;
            CheckCluster(caller, request.UniqueName);
            var setup = caller.Services.GetRequiredService<ClusterSetupService>();
            var store = caller.Services.GetRequiredService<ClusterOperationStore>();
            var actor = caller.User.Identity?.Name ?? "ui";
            var stop = caller.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
            var jobs = caller.Services.GetRequiredService<DashboardSetupJobs>();
            var key = input.Attempt.ToString("N");
            var job = jobs.Start(request, progress => setup.RunAsync(request, key, actor, stop, progress));
            while (!job.IsCompleted && store.Find(request.UniqueName, "cluster.setup", key) is null) await Task.Delay(50, token);
            if (job.IsCompleted)
            {
                try { if ((await job).Error is { } error) throw new QuasarOperationException(409, error.Message); }
                catch (ClusterOperationConflictException error) { throw new QuasarOperationException(409, error.Message); }
            }
            return true;
        }, Read with { Effect = QuasarOperationEffect.Change, Description = "Creates or resumes a managed cluster using a verified world, profile and connected host placements." });
        services.AddQuasarOperation<SetupRegistration, bool>(PluginId, "cluster.setup.register", Manage, async (caller, input, token) =>
        {
            CheckCluster(caller, input.Id);
            try { await caller.Services.GetRequiredService<ClusterCatalog>().CreateAsync(new(input.Id, input.Name, input.Url, input.CredentialReference), token); }
            catch (Exception error) when (error is InvalidDataException or InvalidOperationException or ArgumentException)
            { throw new QuasarOperationException(400, error.Message); }
            return true;
        }, Read with { Effect = QuasarOperationEffect.Change, Description = "Registers an existing cluster using its configured registry address and credential reference." });
    }

    private static async Task<bool> Allowed(QuasarOperationContext caller, string policy) =>
        (await caller.Services.GetRequiredService<IAuthorizationService>().AuthorizeAsync(caller.User, null, policy)).Succeeded;

    private static void CheckCluster(QuasarOperationContext caller, string id)
    {
        if (string.IsNullOrWhiteSpace(id) || !caller.User.CanQueryCluster(id)) throw new QuasarOperationException(403, "Your credentials cannot manage this cluster ID.");
    }
}

public sealed class DashboardSetupJobs(ILogger<DashboardSetupJobs> logger)
{
    public sealed class Job(ClusterSetupRequest request) : IProgress<ManagedRuntimeInstallProgress>
    {
        public ClusterSetupRequest Request => request;
        public volatile ManagedRuntimeInstallProgress? Progress;
        public void Report(ManagedRuntimeInstallProgress value) => Progress = value;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Job> _active = new(StringComparer.OrdinalIgnoreCase);

    public Job? Get(string id) { lock (_gate) return _active.GetValueOrDefault(id); }

    public Task<ClusterOperation> Start(ClusterSetupRequest request, Func<Job, Task<ClusterOperation>> run)
    {
        lock (_gate)
        {
            if (_active.ContainsKey(request.UniqueName)) throw new QuasarOperationException(409, "Setup is already running for this cluster. Open its resume page to follow progress.");
            var job = new Job(request);
            var work = run(job);
            _active.Add(request.UniqueName, job);
            _ = ObserveAsync(job, work);
            return work;
        }
    }

    private async Task ObserveAsync(Job job, Task<ClusterOperation> work)
    {
        try { await work; }
        catch (Exception error) { logger.LogWarning(error, "Setup for cluster {ClusterId} stopped", job.Request.UniqueName); }
        finally { lock (_gate) _active.Remove(job.Request.UniqueName); }
    }
}
