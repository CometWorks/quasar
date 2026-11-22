using Microsoft.AspNetCore.Authorization;
using Quasar.Client.Fleet;
using Quasar.Models;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Services.Auth;
using static Quasar.Services.Plugins.QuasarCoreOperations;

namespace Quasar.Services.Plugins;

public static class QuasarHostDashboardOperations
{
    public sealed record RegistrationInput(string Id, string Name, string Address, string Origin);
    public sealed record HostId(string Id = "");
    private static readonly QuasarOperationMetadata Read = new() { Effect = QuasarOperationEffect.Read, AgentAccess = QuasarAgentAccess.Denied };
    private static readonly QuasarOperationMetadata Change = new() { Effect = QuasarOperationEffect.Change, AgentAccess = QuasarAgentAccess.Denied };
    private static readonly string[] Manage = [QuasarPolicyNames.CanManageSecurity];

    public static void AddQuasarHostDashboardOperations(this IServiceCollection services)
    {
        services.AddQuasarOperation<EmptyRequest, HostSnapshot>(PluginId, "hosts.status", [QuasarPolicyNames.CanView],
            async (caller, _, token) =>
            {
                await RequirePolicy(caller, QuasarPolicyNames.CanView);
                return await Snapshot(caller, token);
            }, Read with { Description = "Lists executor connections, clusters and in-process agents on the Hosts page." });
        services.AddQuasarOperation<HostId, EnrollmentSnapshot>(PluginId, "hosts.enrollments", Manage,
            async (caller, input, token) =>
            {
                await RequirePolicy(caller);
                var services = caller.Services;
                var registered = services.GetRequiredService<ClusterHostCatalog>().GetAll();
                var host = registered.FirstOrDefault(h => h.Id == input.Id);
                bool connected = host is not null && services.GetRequiredService<ClusterHostTunnels>().IsConnected(host.Id);
                string[] warnings = [];
                if (connected && host is not null)
                    try { warnings = (await services.GetRequiredService<ClusterHostClient>().GetStatusAsync(Target(host), token)).Data.Warnings ?? []; }
                    catch (ClusterHostException) { }
                return new(registered.Where(x => x.Id != ClusterLocalHost.HostId).Select(PublicRegistration).ToArray(), connected, warnings);
            }, Read with { Description = "Lists remote host registrations and checks an enrolled executor's connection and warnings." });
        services.AddQuasarOperation<RegistrationInput, HostRegistration>(PluginId, "hosts.register", Manage,
            async (caller, input, token) =>
            {
                await RequirePolicy(caller);
                RequireRemote(input.Id);
                ClusterHostInstaller.ValidateOrigin(input.Origin);
                try
                {
                    return PublicRegistration(await caller.Services.GetRequiredService<ClusterHostCatalog>()
                        .RegisterAsync(input.Id, input.Name, input.Address, 18400, token));
                }
                catch (InvalidOperationException error) { throw new QuasarOperationException(409, error.Message); }
            }, Change with { Description = "Registers a remote host machine for enrollment." });
        services.AddQuasarOperation<HostInstallInput, EnrollmentTicket>(PluginId, "hosts.ticket", Manage,
            async (caller, input, _) =>
            {
                await RequirePolicy(caller);
                RequireRemote(input.Id);
                var ticket = caller.Services.GetRequiredService<ClusterHostInstaller>().Issue(input.Id, input.Origin);
                return new(ticket.Command, ticket.ExpiresAt);
            }, Change with { Description = "Issues a short-lived single-use installation command for an enrolled remote host machine." });
        services.AddQuasarOperation<HostInstallInput, bool>(PluginId, "hosts.install", Manage,
            async (caller, input, token) =>
            {
                await RequirePolicy(caller);
                RequireRemote(input.Id);
                await caller.Services.GetRequiredService<ClusterHostInstaller>().InstallSshAsync(input.Id, input.Origin,
                    new(input.SshAddress ?? "", input.SshUser ?? "", input.SshPort, input.IdentityFile), token);
                return true;
            }, Change with { Description = "Installs the enrolled Host executor over the administrator's trusted SSH connection.", Deadline = TimeSpan.FromMinutes(5) });
        services.AddQuasarRead<HostInstallInput>(PluginId, "hosts.package", Manage,
            async (caller, input, _) =>
            {
                await RequirePolicy(caller);
                RequireRemote(input.Id);
                var installer = caller.Services.GetRequiredService<ClusterHostInstaller>();
                return new QuasarRead(ct =>
                {
                    ct.ThrowIfCancellationRequested();
                    return Task.FromResult<Stream>(new MemoryStream(installer.CreateManualPackage(input.Id, input.Origin), writable: false));
                }, "application/zip", "quasar-host-" + input.Id + ".zip");
            }, Read with { Description = "Downloads the private enrollment package for the selected remote host." });
        services.AddQuasarOperation<HostId, bool>(PluginId, "hosts.remove", Manage,
            async (caller, input, token) =>
            {
                await RequirePolicy(caller);
                var services = caller.Services;
                try
                {
                    await services.GetRequiredService<ClusterHostCatalog>().RemoveAsync(input.Id, services.GetRequiredService<ClusterCatalog>(),
                        services.GetRequiredService<ClusterHostTunnels>(), token);
                }
                catch (InvalidOperationException error) { throw new QuasarOperationException(409, error.Message); }
                catch (KeyNotFoundException) { throw new QuasarOperationException(404, "Host machine is not registered."); }
                return true;
            }, Change with { Description = "Revokes and removes an unused remote Host registration." });
    }

    private static void RequireRemote(string id)
    {
        if (id == ClusterLocalHost.HostId) throw new QuasarOperationException(400, "Quasar manages the local Host executor.");
    }

    private static async Task<bool> HasPolicy(QuasarOperationContext caller, string policy)
    {
        var authorization = caller.Services.GetRequiredService<IAuthorizationService>();
        return (await authorization.AuthorizeAsync(caller.User, null, policy)).Succeeded;
    }

    private static async Task RequirePolicy(QuasarOperationContext caller, string policy = QuasarPolicyNames.CanManageSecurity)
    {
        if (!await HasPolicy(caller, policy)) throw new QuasarOperationException(403, "Host management access was denied.");
    }

    private static HostRegistration PublicRegistration(EnrolledClusterHost host) => new(host.Id, host.Name, host.Address);

    private static ClusterDefinition Target(EnrolledClusterHost host) => new()
    {
        UniqueName = "", HostCommandUrl = host.CommandUrl, HostCommandTokenEnvironmentVariable = host.CredentialReference,
    };

    private static async Task<HostSnapshot> Snapshot(QuasarOperationContext caller, CancellationToken token)
    {
        var services = caller.Services;
        var local = services.GetRequiredService<ClusterLocalHost>();
        local.Request();
        var registrations = services.GetRequiredService<ClusterHostCatalog>().GetAll();
        var client = services.GetRequiredService<ClusterHostClient>();
        bool canManage = await HasPolicy(caller, QuasarPolicyNames.CanManageSecurity);
        var targets = services.GetRequiredService<ClusterCatalog>().GetClusters()
            .Where(c => !string.IsNullOrWhiteSpace(c.HostCommandUrl)).Concat(registrations.Select(Target))
            .DistinctBy(c => c.HostCommandUrl);
        var reports = await Task.WhenAll(targets.Select(async target =>
        {
            var registration = registrations.FirstOrDefault(h => h.CommandUrl == target.HostCommandUrl);
            HostRegistration[] removable = canManage && registration is not null && registration.Id != ClusterLocalHost.HostId
                ? [PublicRegistration(registration)] : [];
            try
            {
                var status = (await client.GetStatusAsync(target, token)).Data;
                var row = new HostExecutor(status.HostId + "\0" + status.ExecutorId, status.HostId, status.ExecutorId, true,
                    status.Attachments.Select(a => a.ClusterId).ToArray(), (status.Gateways ?? []).Select(g => $"{g.ClusterId}: {g.Observed}").ToArray(),
                    status.Attachments.Count(a => a.ActualizationConfigured), status.Attachments.Length, "", removable);
                return (Row: row, Warnings: status.HostId == ClusterLocalHost.HostId ? status.Warnings ?? [] : Array.Empty<string>());
            }
            catch (ClusterHostException error)
            {
                var row = new HostExecutor(registration?.Id ?? target.HostCommandUrl, registration?.Name ?? target.HostCommandUrl, "-", false,
                    string.IsNullOrEmpty(target.UniqueName) ? [] : [target.UniqueName], [], 0, 0, error.Message, removable);
                return (Row: row, Warnings: Array.Empty<string>());
            }
        }));
        var executors = reports.Select(x => x.Row).GroupBy(r => r.Identity, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First() with
            {
                Clusters = group.SelectMany(r => r.Clusters).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                Gateways = group.SelectMany(r => r.Gateways).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                Configured = group.Max(r => r.Configured), Attachments = group.Max(r => r.Attachments),
                Registrations = group.SelectMany(r => r.Registrations).DistinctBy(h => h.Id).ToArray(),
            }).OrderBy(r => r.HostName, StringComparer.OrdinalIgnoreCase).ToArray();
        var agents = services.GetRequiredService<AgentRegistry>().GetAgents()
            .GroupBy(a => a.HostKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => new InProcessHost(g.First().HostDisplayName, g.Select(a => a.ServerKey).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                g.Count(a => a.IsConnected), g.Sum(a => a.Snapshot?.Metrics.PlayersOnline ?? 0)))
            .OrderBy(r => r.HostName, StringComparer.OrdinalIgnoreCase).ToArray();
        var warnings = reports.SelectMany(x => x.Warnings).ToList();
        if (local.Error is { } errorMessage) warnings.Insert(0, errorMessage);
        return new(executors, agents, warnings.ToArray(), canManage);
    }
}
