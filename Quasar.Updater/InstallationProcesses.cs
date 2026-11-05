using System.Diagnostics;
using System.Text.Json;

internal static class InstallationProcesses
{
    internal static int Check(string[] args)
    {
        try
        {
            string Option(string name) { var index = Array.IndexOf(args, name); return index >= 0 && index + 1 < args.Length ? args[index + 1] : throw new ArgumentException("Missing " + name); }
            var root = Path.GetFullPath(Option("--root")).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var data = Path.GetFullPath(Option("--data-dir"));
            var manifest = Path.Combine(data, "service-manifest.json");
            if (File.Exists(manifest))
            {
                using var json = JsonDocument.Parse(File.ReadAllText(manifest));
                if (json.RootElement.TryGetProperty("processId", out var pid))
                {
                    try
                    {
                        using var process = Process.GetProcessById(pid.GetInt32());
                        if (!process.HasExited && json.RootElement.TryGetProperty("startedAtUtc", out var started) &&
                            (started.GetDateTimeOffset().UtcDateTime - process.StartTime.ToUniversalTime()) is var age && age >= TimeSpan.Zero && age < TimeSpan.FromMinutes(2))
                            throw new InvalidOperationException("Stop the existing Quasar application/Bootstrap before replacing its files.");
                    }
                    catch (ArgumentException) { }
                }
            }
            foreach (var process in Process.GetProcessesByName("Quasar"))
            {
                using (process)
                {
                    var executable = process.MainModule?.FileName;
                    if (executable is not null && executable.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                        throw new InvalidOperationException("A Quasar process from this installation is still running. Stop it before replacing files.");
                }
            }
            return 0;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException or ArgumentException or JsonException or System.ComponentModel.Win32Exception)
        { Console.Error.WriteLine(exception.Message); return 3; }
    }
}
