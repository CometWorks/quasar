using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Quasar.Host.Contract.V1;
using Magnetar.Protocol.Runtime;
using Quasar.ClusterDeployment;
using Quasar.Models;

namespace Quasar.Services;

/// <summary>Downloads exact Workshop bytes into a private, disposable deployment input.</summary>
public sealed class ClusterWorkshopModStager(ManagedDedicatedServerRuntimeResolver runtime,
    SteamWorkshopCredentialsCatalog credentials, ManagedRuntimeOptions options, QuasarWorkshopModResolver workshop)
{
    private const string WorkshopAppId = "244850";
    private readonly SemaphoreSlim gate = new(1, 1);
    internal static string StorageDirectory => Path.Combine(MagnetarPaths.GetQuasarManagedRuntimeToolsDirectory(), "ClusterModVersions");
    internal sealed record Receipt(long Id, long Version, Dictionary<string, DeploymentFile> Files);
    public sealed record StagedMods(string Directory, IReadOnlyDictionary<long, long> Versions);


    /// <summary>Hidden items have no observed version; they are pinned to what SteamCMD downloads.</summary>
    public async Task<StagedMods> StageAsync(IReadOnlyList<QuasarModSelection> mods, string work, CancellationToken token,
        IProgress<string>? progress, IReadOnlyDictionary<long, WorkshopUpdateObservation> observations)
    {
        using var activity = await ClusterStorageActivity.BeginAsync(token);
        await gate.WaitAsync(token); // SteamCMD may write its shared HOME even with a private install directory.
        try
        {
            string destination = Path.Combine(work, "mods");
            Directory.CreateDirectory(destination);
            ClusterWorldFiles.Private(destination);
            var versions = new Dictionary<long, long>();
            if (mods.Count == 0) return new(destination, versions);
            using var content = await ClusterContentStore.OpenAsync(StorageDirectory, token);
            int index = 0;
            foreach (var mod in mods)
            {
                long? observed = PinnedVersion(mod.WorkshopId, observations);
                string id = mod.WorkshopId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                string? versionPath = observed is { } known ? Path.Combine(StorageDirectory, id + "-" + known) : null;
                Receipt receipt;
                if (versionPath is not null && Directory.Exists(versionPath))
                {
                    progress?.Report($"Reusing verified mod {++index}/{mods.Count}: {mod.DisplayName} ({id})...");
                    receipt = await ReadReceiptAsync(versionPath, mod.WorkshopId, observed!.Value, token);
                }
                else
                {
                    progress?.Report($"Downloading {(observed is null ? "hidden" : "changed")} mod {++index}/{mods.Count}: {mod.DisplayName} ({id})...");
                    var (source, downloaded) = await DownloadModAsync(id, work, token);
                    long version = observed ?? downloaded ?? throw new InvalidOperationException(
                        $"Workshop version for {id} is unknown: the public Workshop API hides the item, no configured Steam Web API key can see it, and SteamCMD recorded no version for the download.");
                    versionPath = Path.Combine(StorageDirectory, id + "-" + version);
                    var pins = await ClusterDeploymentFiles.InspectAsync(source, token);
                    if (pins.Count == 0) throw new InvalidDataException($"Workshop item {id} has no mod payload.");
                    if ((downloaded ?? version) != version
                        || (PinnedVersion(mod.WorkshopId, await workshop.CheckUpdatesAsync([mod.WorkshopId], token)) ?? version) != version)
                        throw new InvalidOperationException($"Workshop item {id} changed during download. Check updates and stage again.");
                    receipt = new(mod.WorkshopId, version, pins);
                    // A hidden item is downloaded every time; its version may be stored already.
                    if (Directory.Exists(versionPath))
                        receipt = await ReadReceiptAsync(versionPath, mod.WorkshopId, version, token);
                    else
                    {
                        string staging = versionPath + ".stage-" + Guid.NewGuid().ToString("N");
                        Directory.CreateDirectory(staging);
                        try
                        {
                            foreach (var (name, pin) in pins)
                                await content.ReferenceAsync(Path.Combine(source, name), Path.Combine(staging, "payload", name), pin, token);
                            await File.WriteAllBytesAsync(Path.Combine(staging, "receipt.json"),
                                JsonSerializer.SerializeToUtf8Bytes(receipt, ClusterDeploymentFiles.JsonOptions), token);
                            Directory.Move(staging, versionPath);
                        }
                        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
                    }
                }
                versions[mod.WorkshopId] = receipt.Version;
                foreach (var (name, pin) in receipt.Files)
                    await content.ReferenceAsync(Path.Combine(versionPath, "payload", name), Path.Combine(destination, id + ".sbm", name), pin, token);
            }
            return new(destination, versions);
        }
        finally { gate.Release(); }
    }

    /// <summary>The observed version, or null for a hidden item whose download decides it.</summary>
    internal static long? PinnedVersion(long id, IReadOnlyDictionary<long, WorkshopUpdateObservation> observations) =>
        observations.TryGetValue(id, out var observed) && observed.Hidden ? null : ObservedVersion(id, observations);

    internal static long ObservedVersion(long id, IReadOnlyDictionary<long, WorkshopUpdateObservation> observations)
    {
        if (id <= 0 || !observations.TryGetValue(id, out var observed) || observed.Error is not null
            || observed.UpdatedTimestamp is not > 0)
            throw new InvalidOperationException($"Workshop version for {id} is unavailable. Check content updates and stage again.");
        return observed.UpdatedTimestamp.Value;
    }

    internal static async Task<Receipt> ReadReceiptAsync(string path, long id, long version, CancellationToken token)
    {
        ClusterWorldFiles.Private(path);
        var receipt = JsonSerializer.Deserialize<Receipt>(await File.ReadAllBytesAsync(Path.Combine(path, "receipt.json"), token),
            ClusterDeploymentFiles.JsonOptions) ?? throw new InvalidDataException("Missing mod version receipt.");
        if (receipt.Id != id || receipt.Version != version || receipt.Files is null || receipt.Files.Count == 0)
            throw new InvalidDataException("Mod version receipt does not match the requested version.");
        foreach (string name in receipt.Files.Keys) ClusterWorldFiles.Relative(name);
        await ClusterWorldFiles.VerifyAsync(Path.Combine(path, "payload"), receipt.Files, token);
        return receipt;
    }

    private async Task<(string Source, long? Version)> DownloadModAsync(string id, string work, CancellationToken token)
    {
        string steamCmd = runtime.GetInstalledVersions().SteamCmdPath;
        if (string.IsNullOrWhiteSpace(steamCmd) || !File.Exists(steamCmd))
            throw new InvalidOperationException("Install Quasar's managed SteamCMD before staging cluster mods.");
        string install = Path.Combine(work, "workshop-install");
        Directory.CreateDirectory(install);
        ClusterWorldFiles.Private(install);
        Directory.CreateDirectory(options.SteamCmdHomeDirectory);
        ClusterWorldFiles.Private(options.SteamCmdHomeDirectory);
        if (!await DownloadAsync(steamCmd, install, id, null, token))
        {
            var account = credentials.GetCredentials();
            if (string.IsNullOrWhiteSpace(account.SteamUsername) || string.IsNullOrEmpty(account.SteamPassword))
                throw new InvalidOperationException($"SteamCMD could not download Workshop item {id} anonymously. Configure Steam credentials in Steam Workshop settings and stage again.");
            if (!await DownloadAsync(steamCmd, install, id, account, token))
                throw new InvalidOperationException($"SteamCMD could not download Workshop item {id} with the configured account. Check its Workshop access and Steam Guard authentication.");
        }
        string workshopRoot = new[] { install, Path.Combine(options.SteamCmdHomeDirectory, ".steam", "steamcmd"), Path.GetDirectoryName(steamCmd)! }
            .Select(root => Path.Combine(root, "steamapps", "workshop"))
            .FirstOrDefault(root => Directory.Exists(Path.Combine(root, "content", WorkshopAppId, id)))
            ?? throw new InvalidDataException($"Workshop item {id} has no downloaded payload.");
        string manifest = Path.Combine(workshopRoot, "appworkshop_" + WorkshopAppId + ".acf");
        return (Path.Combine(workshopRoot, "content", WorkshopAppId, id),
            File.Exists(manifest) ? DownloadedVersion(await File.ReadAllTextAsync(manifest, token), id) : null);
    }

    /// <summary>The timeupdated SteamCMD recorded for the item; the same value the Workshop API reports.</summary>
    internal static long? DownloadedVersion(string appWorkshopAcf, string id)
    {
        int installed = appWorkshopAcf.IndexOf("\"WorkshopItemsInstalled\"", StringComparison.Ordinal);
        if (installed < 0) return null;
        var match = Regex.Match(appWorkshopAcf[installed..], "\"" + Regex.Escape(id) + "\"\\s*\\{[^}]*?\"timeupdated\"\\s*\"(\\d+)\"");
        return match.Success && long.TryParse(match.Groups[1].Value, out long version) && version is > 0 and <= 253402300799
            ? version : null;
    }

    private async Task<bool> DownloadAsync(string steamCmd, string install, string id,
        SteamWorkshopCredentials? account, CancellationToken token)
    {
        // A private runscript keeps the password out of process arguments and diagnostic logs.
        string script = Path.Combine(install, ".steamcmd-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            string login = account is null ? "login anonymous" : "login " + ScriptToken(account.SteamUsername) + " " + ScriptToken(account.SteamPassword);
            var fileOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var file = new FileStream(script, fileOptions))
            await using (var writer = new StreamWriter(file))
                await writer.WriteAsync((login + "\nworkshop_download_item " + WorkshopAppId + " " + id + "\nquit\n").AsMemory(), token);
            var start = ManagedDedicatedServerRuntimeResolver.CreateSteamCmdStartInfo(steamCmd,
                "+force_install_dir \"" + install.Replace("\"", "\\\"", StringComparison.Ordinal) + "\" +runscript \"" + script + "\"",
                options.SteamCmdHomeDirectory);
            start.WorkingDirectory = install;
            using var process = Process.Start(start) ?? throw new InvalidOperationException("SteamCMD did not start.");
            // Drain both pipes; SteamCMD can otherwise block while writing diagnostics.
            Task<string> output = process.StandardOutput.ReadToEndAsync(token);
            Task<string> error = process.StandardError.ReadToEndAsync(token);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromMinutes(15));
            try { await process.WaitForExitAsync(deadline.Token); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                throw;
            }
            await Task.WhenAll(output, error);
            return process.ExitCode == 0 &&
                (output.Result.Contains("Success. Downloaded item", StringComparison.OrdinalIgnoreCase)
                 || error.Result.Contains("Success. Downloaded item", StringComparison.OrdinalIgnoreCase));
        }
        finally { File.Delete(script); }
    }

    private static string ScriptToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(c => c is '\n' or '\r' or '"' or '\\'))
            throw new InvalidDataException("Steam account credentials contain unsupported characters.");
        return "\"" + value + "\"";
    }
}
