using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Quasar.Updates;

// Native service control is deliberately bound to protected installer metadata.
// The updater never executes a command, service name or destination from an intent.
internal static class NativeQuasarServices
{
    internal sealed record Status(int Pid, bool Running, string Executable, string Account);
    internal static async Task<Status> ReadAsync(QuasarServiceInstallation installation, bool updater, CancellationToken token)
    {
        var name = updater ? installation.UpdaterService : installation.MainService;
        if (OperatingSystem.IsWindows())
        {
            var manager = OpenSCManager(null, null, 1);
            if (manager == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
            try
            {
                var service = OpenService(manager, name, 1 | 4);
                if (service == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
                try
                {
                    QueryServiceConfig(service, IntPtr.Zero, 0, out var size);
                    var buffer = Marshal.AllocHGlobal((int)size);
                    try
                    {
                        if (!QueryServiceConfig(service, buffer, size, out _)) throw new System.ComponentModel.Win32Exception();
                        var config = Marshal.PtrToStructure<ServiceConfig>(buffer);
                        if (!QueryServiceStatusEx(service, 0, out var status, (uint)Marshal.SizeOf<ServiceStatus>(), out _)) throw new System.ComponentModel.Win32Exception();
                        return new((int)status.Pid, status.State == 4, Marshal.PtrToStringUni(config.BinaryPath) ?? "", Marshal.PtrToStringUni(config.Account) ?? "");
                    }
                    finally { Marshal.FreeHGlobal(buffer); }
                }
                finally { CloseServiceHandle(service); }
            }
            finally { CloseServiceHandle(manager); }
        }
        var properties = await SystemdPropertiesAsync(installation, name, token);
        return new(int.TryParse(properties.GetValueOrDefault("MainPID"), out var pid) ? pid : 0,
            properties.GetValueOrDefault("ActiveState") == "active", properties.GetValueOrDefault("ExecStart", ""), properties.GetValueOrDefault("User", ""));
    }

    internal static async Task<BootstrapRestartCapability> VerifyAsync(QuasarServiceInstallation installation, int mainPid, CancellationToken token)
    {
        var failure = "";
        try
        {
            var main = await ReadAsync(installation, false, token);
            var updater = await ReadAsync(installation, true, token);
            if (!main.Running || main.Pid != mainPid || !updater.Running || updater.Pid <= 0)
                throw new InvalidOperationException("The installed main and updater services must both be running; the main service must own this Quasar process.");
            var updaterExecutable = Path.Combine(installation.InstallDirectory, "Updater", OperatingSystem.IsWindows() ? "Quasar.Updater.exe" : "Quasar.Updater");
            if (OperatingSystem.IsWindows())
            {
                if (!string.Equals(main.Executable, $"\"{installation.Executable}\" serve --service", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(updater.Executable, $"\"{updaterExecutable}\" --installation \"{installation.MetadataPath}\"", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(main.Account, installation.Account, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(updater.Account, installation.UpdaterAccount, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Service executable/action or execution account differs from the installation metadata.");
                VerifyWindowsRecovery(installation.MainService);
                VerifyWindowsRecovery(installation.UpdaterService);
            }
            else
            {
                var properties = await SystemdPropertiesAsync(installation, installation.MainService, token);
                var group = properties.GetValueOrDefault("ControlGroup");
                var actualGroups = File.ReadAllLines($"/proc/{mainPid}/cgroup").Select(line => line.Split(':', 3).Last());
                if (!actualGroups.Contains(group)) throw new InvalidOperationException("The configured systemd unit does not own this process.");
                if (!main.Executable.Contains("path=/usr/bin/env ;", StringComparison.Ordinal) ||
                    !main.Executable.Contains($"argv[]=/usr/bin/env {installation.Executable} serve --service ;", StringComparison.Ordinal) ||
                    !updater.Executable.Contains("path=/usr/bin/env ;", StringComparison.Ordinal) ||
                    !updater.Executable.Contains($"argv[]=/usr/bin/env {updaterExecutable} --installation {installation.MetadataPath} ;", StringComparison.Ordinal) ||
                    File.ResolveLinkTarget($"/proc/{main.Pid}/exe", true)?.FullName != installation.Executable ||
                    File.ResolveLinkTarget($"/proc/{updater.Pid}/exe", true)?.FullName != updaterExecutable)
                    throw new InvalidOperationException("The systemd actions and live executable paths must match the installed Quasar/updater (env must exec them in place).");
                if (properties.GetValueOrDefault("Restart") != "on-failure" || properties.GetValueOrDefault("KillMode") != "process" ||
                    System.Text.RegularExpressions.Regex.IsMatch(properties.GetValueOrDefault("SuccessExitStatus", "") + " " + properties.GetValueOrDefault("RestartPreventExitStatus", ""), @"\b75\b"))
                    throw new InvalidOperationException("The main unit needs Restart=on-failure, KillMode=process, and failure exit 75 must not count as success.");
                var uid = File.ReadLines($"/proc/{mainPid}/status").Single(line => line.StartsWith("Uid:")).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[1];
                var accountUid = (await BootstrapUpdateSupport.RunAsync("id", ["-u", "--", installation.Account], token)).Output.Trim();
                if (uid != accountUid || main.Account.Length > 0 && main.Account != installation.Account)
                    throw new InvalidOperationException("The main service execution account does not match this process.");
                var updaterUid = File.ReadLines($"/proc/{updater.Pid}/status").Single(line => line.StartsWith("Uid:")).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[1];
                var expectedUpdaterUid = (await BootstrapUpdateSupport.RunAsync("id", ["-u", "--", installation.UpdaterAccount], token)).Output.Trim();
                if (updaterUid != expectedUpdaterUid) throw new InvalidOperationException("The updater execution account does not match its live process.");
                var updaterProperties = await SystemdPropertiesAsync(installation, installation.UpdaterService, token);
                if (updaterProperties.GetValueOrDefault("Restart") != "on-failure" || updater.Account.Length > 0 && updater.Account != installation.UpdaterAccount)
                    throw new InvalidOperationException("Repair the updater account and Restart=on-failure policy.");
            }
            return new() { CanRestart = true, Supervisor = OperatingSystem.IsWindows() ? $"Windows Services {installation.MainService} + {installation.UpdaterService}" : $"systemd {(installation.UserService ? "user" : "system")} units {installation.MainService} + {installation.UpdaterService}", Detail = "Verified native service ownership, executable actions, execution accounts and failure recovery.", ProcessId = mainPid };
        }
        catch (Exception exception) when (exception is not OperationCanceledException) { failure = exception.Message; }
        return BootstrapUpdateSupport.Unavailable(failure) with { Repair = "Run the current installer in repair mode with this installation's existing service names, paths and account/scope. Foreground launches can stage safely and require manual restart." };
    }

    internal static async Task StopAsync(QuasarServiceInstallation installation, CancellationToken token)
    {
        if (OperatingSystem.IsWindows())
            await PowerShellAsync("$s=Get-Service -Name $env:QUASAR_SERVICE_NAME; if($s.Status -ne 'Stopped'){$s.Stop();$s.WaitForStatus('Stopped',[TimeSpan]::FromSeconds(90))}", installation.MainService, token, 100);
        else await SystemctlAsync(installation, ["stop", installation.MainService], token);
    }
    internal static async Task StartAsync(QuasarServiceInstallation installation, CancellationToken token)
    {
        if (OperatingSystem.IsWindows())
            await PowerShellAsync("$s=Get-Service -Name $env:QUASAR_SERVICE_NAME; $s.Start();$s.WaitForStatus('Running',[TimeSpan]::FromSeconds(60))", installation.MainService, token, 70);
        else await SystemctlAsync(installation, ["start", installation.MainService], token);
    }

    private static async Task<Dictionary<string, string>> SystemdPropertiesAsync(QuasarServiceInstallation installation, string name, CancellationToken token)
    {
        var output = await SystemctlAsync(installation, ["show", name, "--no-pager", "--property=MainPID,ActiveState,ExecStart,User,Restart,KillMode,ControlGroup,SuccessExitStatus,RestartPreventExitStatus"], token);
        return output.Split('\n').Where(line => line.Contains('=')).Select(line => line.Split('=', 2)).ToDictionary(parts => parts[0], parts => System.Text.RegularExpressions.Regex.Replace(parts[1], @"\\x([0-9a-fA-F]{2})", m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString()));
    }
    private static async Task<string> SystemctlAsync(QuasarServiceInstallation installation, string[] arguments, CancellationToken token)
    {
        var args = installation.UserService ? new[] { "--user" }.Concat(arguments) : arguments;
        var result = await BootstrapUpdateSupport.RunAsync("systemctl", args, token, timeoutSeconds: 100);
        if (result.ExitCode != 0) throw new InvalidOperationException(result.Output.Trim());
        return result.Output;
    }
    private static async Task<string> PowerShellAsync(string script, string name, CancellationToken token, int timeout = 20)
    {
        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var result = await BootstrapUpdateSupport.RunAsync(exe, ["-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes("$ErrorActionPreference='Stop';" + script))], token, new() { ["QUASAR_SERVICE_NAME"] = name }, timeout);
        if (result.ExitCode != 0) throw new InvalidOperationException(result.Output.Trim());
        return result.Output;
    }

    // Query native SCM recovery data, rather than parsing localized sc.exe text.
    private static void VerifyWindowsRecovery(string name)
    {
        var manager = OpenSCManager(null, null, 1);
        if (manager == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        try
        {
            var service = OpenService(manager, name, 1);
            if (service == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
            try
            {
                QueryServiceConfig2(service, 2, IntPtr.Zero, 0, out var size);
                var buffer = Marshal.AllocHGlobal((int)size);
                try
                {
                    if (!QueryServiceConfig2(service, 2, buffer, size, out _)) throw new System.ComponentModel.Win32Exception();
                    var actions = Marshal.PtrToStructure<FailureActions>(buffer);
                    if (actions.Count == 0 || actions.Actions == IntPtr.Zero) throw new InvalidOperationException($"Service {name} has no failure recovery.");
                    for (var i = 0; i < actions.Count; i++)
                        if (Marshal.ReadInt32(actions.Actions, i * 8) != 1) throw new InvalidOperationException($"Service {name} must restart on failure without reboot or command actions.");
                    if (!QueryServiceConfig2(service, 4, buffer, size, out _) || Marshal.ReadInt32(buffer) == 0)
                        throw new InvalidOperationException($"Service {name} must apply recovery to reported nonzero service failures as well as crashes.");
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct ServiceConfig { public uint Type, StartType, ErrorControl; public IntPtr BinaryPath, LoadOrder; public uint Tag; public IntPtr Dependencies, Account, DisplayName; }
    [StructLayout(LayoutKind.Sequential)] private struct ServiceStatus { public uint Type, State, Accepted, Win32Exit, ServiceExit, Checkpoint, WaitHint, Pid, Flags; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryServiceConfig(IntPtr service, IntPtr buffer, uint size, out uint needed);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool QueryServiceStatusEx(IntPtr service, uint level, out ServiceStatus status, uint size, out uint needed);
    [StructLayout(LayoutKind.Sequential)] private struct FailureActions { public uint ResetPeriod; public IntPtr Reboot; public IntPtr Command; public uint Count; public IntPtr Actions; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenService(IntPtr manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool QueryServiceConfig2(IntPtr service, uint level, IntPtr buffer, uint size, out uint needed);
    [DllImport("advapi32.dll")] private static extern bool CloseServiceHandle(IntPtr handle);
}
