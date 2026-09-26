using System.Security.Claims;
using Quasar.Models;
using Quasar.Services.Auth;

namespace Quasar.Services.Updates;

internal sealed record UpdateNotice(string Key, string Title, string Body, string Url);

internal static class UpdateNotices
{
    internal static UpdateNotice? Current(QuasarUpdateSnapshot updates, ClusterReleaseSnapshot releases,
        IEnumerable<ClusterDefinition> clusters, ClaimsPrincipal user)
    {
        if (!string.IsNullOrWhiteSpace(updates.GitHubTokenWarning))
            return new("token:" + updates.GitHubTokenWarning, "Quasar update credentials",
                updates.GitHubTokenWarning, "/settings/updates");

        var web = updates.WebReleases.FirstOrDefault(candidate => candidate.IsNewer && candidate.IsStaged)
            ?? updates.WebReleases.FirstOrDefault(candidate => candidate.IsNewer);
        if (web is not null)
            return new("web:" + web.Version + ":" + web.IsStaged, "Quasar update available",
                web.IsStaged ? $"Quasar UI {web.Version} is staged. Open Updates to activate."
                    : $"Quasar UI {web.Version} is ready to download.", "/settings/updates");

        if (updates.Bootstrap is { } bootstrap)
            return new("launcher:" + bootstrap.Version, "Quasar launcher update available",
                $"Quasar launcher {bootstrap.Version} update available.", "/settings/updates");

        var cluster = clusters.Where(candidate => user.CanQueryCluster(candidate.UniqueName)
                && ClusterReleaseMonitor.IsUpdateAvailable(candidate, releases.Release))
            .OrderBy(candidate => candidate.UniqueName, StringComparer.Ordinal).FirstOrDefault();
        return cluster is null || releases.Release is null ? null
            : new("cluster:" + cluster.UniqueName + ":" + releases.Release.Version,
                "Cluster update available", $"Cluster package {releases.Release.Version} available for {cluster.DisplayName}.",
                "/clusters/" + Uri.EscapeDataString(cluster.UniqueName));
    }
}
