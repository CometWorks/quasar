using System.Text.Json;
using System.Text.RegularExpressions;
using Admin = CometWorks.ClusterGateway.AdminContract.V1;
using HostContract = global::Quasar.Host.Contract.V1;

namespace Quasar.Host;

/// <summary>
/// Clean cluster stop at machine shutdown and logout. The Host unit keeps <c>KillMode=process</c> so
/// a Host restart or update leaves the clusters running, but then systemd SIGKILLs the leftover
/// Gateway and nodes when it tears down the user manager. The installer adds a companion unit that is
/// only stopped at that teardown; its <c>ExecStop</c> runs <see cref="StopClustersAsync"/> while the
/// Host is still up.
/// </summary>
internal static class HostShutdown
{
    // The Gateway spends up to 25 s returning players to the menu, drains for GraceSeconds, then
    // gives up 30 s later. That fits StopTimeoutSeconds, which stays under the 120 s that systemd
    // gives user@.service to stop by default. Keep in sync with the stop unit in ClusterHostInstaller.
    internal const int GraceSeconds = 30;
    internal const int StopTimeoutSeconds = 110;

    internal static async Task<int> StopClustersAsync(string configPath)
    {
        HostExecutorConfig config;
        AttachmentStore attachments;
        HostContract.GatewaySpec[] gateways;
        try
        {
            config = Program.Load(configPath);
            HostCredentials.Load(config.StateDirectory);
            attachments = new AttachmentStore(config.StateDirectory, config.Attachments);
            gateways = new GatewaySpecStore(config.StateDirectory).GetAll();
        }
        catch (Exception exception) when (exception is IOException or JsonException or ArgumentException
            or InvalidDataException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(StopTimeoutSeconds - 5));
        bool[] stopped = await Task.WhenAll(gateways.Where(spec => spec.Goal == HostContract.GatewayGoal.On)
            .Select(spec => StopAsync(client, spec, attachments, deadline.Token)));
        return stopped.All(ok => ok) ? 0 : 1;
    }

    private static async Task<bool> StopAsync(HttpClient client, HostContract.GatewaySpec spec,
        AttachmentStore attachments, CancellationToken cancellationToken)
    {
        string cluster = spec.ClusterId;
        try
        {
            // The Gateway's admin API shares the listener the executor heartbeats go to.
            HostContract.HostAttachmentSpec attachment = attachments.GetAll().FirstOrDefault(item =>
                    item.ClusterId.Equals(cluster, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("no attachment names the Gateway URL");
            string? variable = null;
            if (ExecutionBundle.ReadManifest(spec.BundleManifestPath, spec.BundleManifestSha256).Gateway?
                    .SecretEnvironment?.TryGetValue("CLUSTER_ADMIN_TOKEN", out variable) != true)
                throw new InvalidDataException("the Gateway manifest has no admin credential");
            string token = Environment.GetEnvironmentVariable(variable!)
                ?? throw new InvalidOperationException("the Gateway admin credential is not installed");

            Task<Admin.AdminEnvelope<T>> Send<T>(HttpMethod method, string route, object? body = null, string? key = null) =>
                Program.SendAsync<T>(client, attachment, token, method, Admin.AdminProtocol.RoutePrefix + "/" + route,
                    body, cancellationToken, key);

            Admin.ClusterStatus status = (await Send<Admin.ClusterStatus>(HttpMethod.Get, "status")).Data;
            if (status.Phase == Admin.ClusterPhase.Down)
            {
                Console.WriteLine($"cluster={cluster} already down");
                return true;
            }
            Console.WriteLine($"cluster={cluster} shutting down for host stop");
            Admin.AdminOperation operation = (await Send<Admin.AdminOperation>(HttpMethod.Post, "shutdown",
                new Admin.ShutdownRequest(GraceSeconds: GraceSeconds), "host-stop-" + Guid.NewGuid().ToString("N"))).Data;
            while (operation.State == Admin.AdminOperationState.Running)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                operation = (await Send<Admin.AdminOperation>(HttpMethod.Get,
                    "operations/" + Uri.EscapeDataString(operation.OperationId))).Data;
            }
            if (operation.State == Admin.AdminOperationState.Succeeded)
            {
                Console.WriteLine($"cluster={cluster} down");
                return true;
            }
            Console.Error.WriteLine($"cluster={cluster} shutdown failed: {operation.Error?.Message}");
            return false;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(cancellationToken.IsCancellationRequested
                ? $"cluster={cluster} shutdown did not finish in time"
                : $"cluster={cluster} shutdown failed: {exception.Message}");
            return false;
        }
    }

    private static readonly object TimeoutLock = new();
    private static (double? Seconds, long ReadAt)? _userManagerStopTimeout;

    /// <summary>What keeps clean stops from working on this machine. Only the guided install runs
    /// the Host as a systemd user service; another service manager gets no checks.</summary>
    internal static string[] Warnings(string hostId)
    {
        if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("INVOCATION_ID") is null)
            return [];
        var warnings = new List<string>();
        string user = Environment.UserName;
        if (!File.Exists("/var/lib/systemd/linger/" + user))
            warnings.Add($"User lingering is off for '{user}', so this Host and its clusters stop when that user logs out."
                + $" Enable it with: sudo loginctl enable-linger {user}");
        string unit = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "systemd", "user", $"quasar-host-{hostId}-stop.service");
        if (!File.Exists(unit))
            warnings.Add("The cluster stop unit is missing, so a reboot kills the clusters without a save."
                + " Run the Host enrollment again to install it.");
        else if (UserManagerStopTimeout() is { } timeout && timeout < StopTimeoutSeconds)
            // Drop-ins apply in file name order, so only this name overrides Ubuntu's 5 s timeout.conf.
            warnings.Add($"systemd gives this user's services {timeout:0} s to stop at shutdown, but a clean cluster save"
                + $" needs {StopTimeoutSeconds} s, so the clusters are killed without a save. As root, write \"[Service]\","
                + " \"TimeoutStopSec=150\" to /etc/systemd/system/user@.service.d/timeout.conf and run systemctl daemon-reload.");
        return warnings.ToArray();
    }

    // Quasar polls the status often; a fixed timeout shows up within a minute.
    private static double? UserManagerStopTimeout()
    {
        lock (TimeoutLock)
        {
            if (_userManagerStopTimeout is not { } cached || Environment.TickCount64 - cached.ReadAt > 60_000)
                _userManagerStopTimeout = cached = (ReadUserManagerStopTimeout(), Environment.TickCount64);
            return cached.Seconds;
        }
    }

    private static double? ReadUserManagerStopTimeout()
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("sh",
                ["-c", "systemctl show \"user@$(id -u).service\" --property=TimeoutStopUSec --value"])
                { RedirectStandardOutput = true, UseShellExecute = false })!;
            string value = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 ? ParseSystemdSeconds(value) : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>Parses systemd's time span output, such as "2min", "1min 30s" or "500ms".
    /// Returns null for "infinity" and anything else it does not understand.</summary>
    internal static double? ParseSystemdSeconds(string value)
    {
        var parts = Regex.Matches(value.Trim(), @"\G\s*(\d+)(us|ms|s|min|h)");
        if (parts.Count == 0 || parts.Sum(part => part.Length) != value.Trim().Length)
            return null;
        return parts.Sum(part => double.Parse(part.Groups[1].Value) * part.Groups[2].Value switch
        {
            "us" => 1e-6, "ms" => 1e-3, "s" => 1, "min" => 60, _ => 3600,
        });
    }
}
