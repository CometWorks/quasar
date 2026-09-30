using System.Diagnostics;
using Magnetar.Protocol.Runtime;
using Quasar.ClusterDeployment;
using Quasar.Models;

namespace Quasar.Services;

/// <summary>Downloads exact Workshop bytes into a private, disposable deployment input.</summary>
public sealed class ClusterWorkshopModStager(ManagedDedicatedServerRuntimeResolver runtime,
    SteamWorkshopCredentialsCatalog credentials, ManagedRuntimeOptions options)
{
    private const string WorkshopAppId = "244850";

    public async Task<string> StageAsync(IReadOnlyList<QuasarModSelection> mods, string work, CancellationToken token)
    {
        string destination = Path.Combine(work, "mods");
        Directory.CreateDirectory(destination);
        ClusterWorldFiles.Private(destination);
        if (mods.Count == 0) return destination;
        string steamCmd = runtime.GetInstalledVersions().SteamCmdPath;
        if (string.IsNullOrWhiteSpace(steamCmd) || !File.Exists(steamCmd))
            throw new InvalidOperationException("Install Quasar's managed SteamCMD before staging cluster mods.");
        string install = Path.Combine(work, "workshop-install");
        Directory.CreateDirectory(install);
        ClusterWorldFiles.Private(install);
        Directory.CreateDirectory(options.SteamCmdHomeDirectory);
        ClusterWorldFiles.Private(options.SteamCmdHomeDirectory);
        var account = credentials.GetCredentials();
        foreach (var mod in mods)
        {
            if (mod.WorkshopId <= 0) throw new InvalidDataException("Workshop mod ID must be positive.");
            string id = mod.WorkshopId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!await DownloadAsync(steamCmd, install, id, null, token))
            {
                if (string.IsNullOrWhiteSpace(account.SteamUsername) || string.IsNullOrEmpty(account.SteamPassword))
                    throw new InvalidOperationException($"SteamCMD could not download Workshop item {id} anonymously. Configure Steam credentials in Steam Workshop settings and stage again.");
                if (!await DownloadAsync(steamCmd, install, id, account, token))
                    throw new InvalidOperationException($"SteamCMD could not download Workshop item {id} with the configured account. Check its Workshop access and Steam Guard authentication.");
            }
            // SteamCMD builds differ in whether Workshop content is written under
            // force_install_dir, its HOME, or the SteamCMD installation directory.
            string source = new[] { install, Path.Combine(options.SteamCmdHomeDirectory, ".steam", "steamcmd"),
                    Path.GetDirectoryName(steamCmd)! }
                .Select(root => Path.Combine(root, "steamapps", "workshop", "content", WorkshopAppId, id))
                .FirstOrDefault(Directory.Exists) ?? "";
            if (!Directory.Exists(source) || !Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Any())
                throw new InvalidDataException($"SteamCMD reported success but Workshop item {id} has no mod payload.");
            await ClusterWorldFiles.CopyAsync(source, Path.Combine(destination, id + ".sbm"), token);
        }
        return destination;
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
