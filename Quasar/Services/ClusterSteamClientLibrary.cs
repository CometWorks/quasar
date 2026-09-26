using System.Security.Cryptography;
using Magnetar.Protocol.Runtime;
using Quasar.Models;

namespace Quasar.Services;

/// <summary>Gives the Gateway Host Valve's steamclient.so, which the Gateway's Steam frontend loads from
/// ~/.steam/sdk64. The cluster release cannot ship it and a cluster machine usually has neither Steam nor
/// SteamCMD, so without this the Gateway exits at start with "Steam GameServer.Init failed".</summary>
public static class ClusterSteamClientLibrary
{
    /// <summary>Quasar's managed SteamCMD first, then the Steam runtime of the account Quasar runs as.</summary>
    internal static string? FindSource(string? steamCmdDirectory = null, string? home = null)
    {
        steamCmdDirectory ??= MagnetarPaths.GetQuasarManagedSteamCmdInstallDirectory();
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new[] { Path.Combine(steamCmdDirectory, "linux64", "steamclient.so"), Path.Combine(home, ".steam", "sdk64", "steamclient.so") }
            .FirstOrDefault(File.Exists);
    }

    /// <summary>Returns a note for the operator when nothing was sent, otherwise null.</summary>
    public static async Task<string?> ProvisionAsync(ClusterHostClient hosts, ClusterDefinition gatewayHost,
        ClusterTestFrontend frontend, ILogger? logger, CancellationToken token, string? source = null)
    {
        if (!frontend.UsesSteam) return null; // Direct Transport only: the Gateway never loads the Steam client.
        source ??= FindSource();
        if (source is null)
        {
            const string note = "steamclient.so was not found in Quasar's managed SteamCMD (linux64/steamclient.so). The Gateway's Steam frontend starts only if the Host account already has ~/.steam/sdk64/steamclient.so.";
            logger?.LogWarning("Cluster {Cluster}: {Note}", gatewayHost.UniqueName, note);
            return note;
        }
        var status = (await hosts.GetStatusAsync(gatewayHost, token)).Data;
        if (!status.SteamClientLibrary)
            throw new InvalidOperationException("Gateway Host cannot receive steamclient.so yet. Enrolled Hosts check Quasar for updates every 15 minutes; resume setup after this Host updates. Older remote enrollments need one final reinstall from Hosts → Add cluster machine to enable automatic updates.");
        string hash;
        await using (var file = File.OpenRead(source)) hash = Convert.ToHexString(await SHA256.HashDataAsync(file, token)).ToLowerInvariant();
        var result = await hosts.InstallSteamClientLibraryAsync(gatewayHost, source, hash, token);
        logger?.LogInformation("Cluster {Cluster}: steamclient.so {Action} at {Path} on Host {Host}.", gatewayHost.UniqueName,
            result.Installed ? "installed" : "already present", result.Path, status.HostId);
        return null;
    }
}
