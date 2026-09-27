using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Quasar.Models;
using Quasar.Services.Auth;

namespace Quasar.Services.Updates;

internal sealed record UpdateNotice(string Key, string Title, string Body, string Url);

internal static class UpdateNotices
{
    internal static UpdateNotice? Current(QuasarUpdateSnapshot updates, ClusterReleaseSnapshot releases,
        IEnumerable<ClusterDefinition> clusters, ClaimsPrincipal user) =>
        All(updates, releases, clusters, user).FirstOrDefault();

    internal static IEnumerable<UpdateNotice> All(QuasarUpdateSnapshot updates, ClusterReleaseSnapshot releases,
        IEnumerable<ClusterDefinition> clusters, ClaimsPrincipal user,
        Func<ClusterDefinition, ClusterContentSnapshot>? content = null)
    {
        if (!string.IsNullOrWhiteSpace(updates.GitHubTokenWarning))
            yield return new("token:" + updates.GitHubTokenWarning, "Quasar update credentials",
                updates.GitHubTokenWarning, "/settings/updates");

        var web = updates.WebReleases.FirstOrDefault(candidate => candidate.IsNewer && candidate.IsStaged)
            ?? updates.WebReleases.FirstOrDefault(candidate => candidate.IsNewer);
        if (web is not null)
            yield return new("web:" + web.Version + ":" + web.IsStaged, "Quasar update available",
                web.IsStaged ? $"Quasar UI {web.Version} is staged. Open Updates to activate."
                    : $"Quasar UI {web.Version} is ready to download.", "/settings/updates");

        if (updates.Bootstrap is { } bootstrap)
            yield return new("launcher:" + bootstrap.Version, "Quasar launcher update available",
                $"Quasar launcher {bootstrap.Version} update available.", "/settings/updates");

        foreach (var cluster in clusters.Where(candidate => user.CanQueryCluster(candidate.UniqueName))
                     .OrderBy(candidate => candidate.UniqueName, StringComparer.Ordinal))
        {
            if (releases.Release is { } release && ClusterReleaseMonitor.IsUpdateAvailable(cluster, release))
                yield return new("cluster:" + cluster.UniqueName + ":" + release.Version,
                    "Cluster update available", $"Cluster package {release.Version} available for {cluster.DisplayName}.",
                    "/clusters/" + Uri.EscapeDataString(cluster.UniqueName));
            var snapshot = content?.Invoke(cluster);
            if (cluster.ActiveDeployment is null || snapshot?.DeploymentRevision != cluster.ActiveDeployment.Revision) continue;
            var changed = snapshot.Items.Where(item => item.HasUpdate).OrderBy(item => item.Kind, StringComparer.Ordinal)
                .ThenBy(item => item.Id, StringComparer.Ordinal).ToArray();
            if (changed.Length == 0) continue;
            string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                System.Text.Json.JsonSerializer.Serialize(changed.Select(item => new
                    { item.Kind, item.Id, item.PinnedVersion, item.BaselineVersion, item.LatestVersion }))))).ToLowerInvariant();
            yield return new("content:" + cluster.UniqueName + ":" + key, "Cluster content changes available",
                $"{cluster.DisplayName}: {changed.Count(i => i.Kind == "mod")} mod change(s), {changed.Count(i => i.Kind == "plugin")} plugin update(s). Review before updating.",
                "/clusters/" + Uri.EscapeDataString(cluster.UniqueName) + "#cluster-content");
        }
    }
}
