using System.Diagnostics;
using System.Text.Json;
using Magnetar.Protocol.Runtime;
using Quasar.Updates;

internal static class RecoverInstallation
{
    internal static async Task<int> RunAsync(string[] args)
    {
        try
        {
            string? Option(string name) { var index = Array.IndexOf(args, name); return index >= 0 && index + 1 < args.Length ? args[index + 1] : null; }
            QuasarServiceInstallation? installation = null;
            if (Option("--installation") is { } metadata)
            {
                installation = QuasarServiceInstallation.Load(metadata, true) ?? throw new InvalidDataException("Installation metadata is missing.");
                if ((await NativeQuasarServices.ReadAsync(installation, false, CancellationToken.None)).Running ||
                    (await NativeQuasarServices.ReadAsync(installation, true, CancellationToken.None)).Running)
                    throw new InvalidOperationException("Stop both Quasar and its updater service before internal recovery.");
            }
            var root = installation?.InstallDirectory ?? Path.GetFullPath(Option("--root") ?? throw new ArgumentException("Specify --installation <metadata> or --root <stopped manual installation>."));
            var data = installation?.DataDirectory ?? Path.GetFullPath(Option("--data-dir") ?? root);
            Environment.SetEnvironmentVariable("QUASAR_INSTALL_DIR", data);
            var manifest = Path.Combine(data, "service-manifest.json");
            if (File.Exists(manifest))
            {
                using var json = JsonDocument.Parse(File.ReadAllText(manifest));
                var pid = json.RootElement.GetProperty("processId").GetInt32();
                try { using var process = Process.GetProcessById(pid); if (!process.HasExited) throw new InvalidOperationException("A recorded Quasar process is still running. Stop it before recovery."); }
                catch (ArgumentException) { }
            }
            using var lease = await QuasarMaintenanceLease.AcquireAsync(CancellationToken.None);
            LauncherReplacement.Restore(root);
            LauncherReplacement.RestoreSettings(root, data);
            QuasarUpdateJournal.Fail("The retained complete application was restored manually. Start Quasar and inspect update --status.");
            Console.WriteLine("Previous application files and configuration restored. Start Quasar and check update --status.");
            return 0;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or ArgumentException or JsonException)
        { Console.Error.WriteLine(exception.Message); return 3; }
    }
}
