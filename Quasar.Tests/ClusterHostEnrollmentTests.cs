using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Quasar.Services;
using Xunit;

namespace Quasar.Tests;

public sealed class ClusterHostEnrollmentTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "host-enrollment-" + Guid.NewGuid().ToString("N"));
    private readonly ClusterCredentialStore credentials;
    private readonly ClusterHostCatalog hosts;
    public ClusterHostEnrollmentTests()
    {
        Directory.CreateDirectory(root);
        credentials = new(new EphemeralDataProtectionProvider(), Path.Combine(root, "credentials.json"));
        hosts = new(credentials, Path.Combine(root, "hosts"));
    }
    public void Dispose() => Directory.Delete(root, true);

    private ClusterLocalHost LocalHost(string binary, string installRoot, ClusterHostCatalog? catalog = null,
        ClusterCredentialStore? store = null, bool preserve = true)
    {
        store ??= credentials; catalog ??= hosts;
        return new(catalog, store, new ClusterHostInstaller(catalog, store, binary, TimeProvider.System), new ClusterHostTunnels(),
            new WebServiceOptions { PreserveManagedServersOnShutdown = preserve },
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ClusterLocalHost>.Instance, Path.Combine(installRoot, "LocalHost"), supported: true);
    }

    // Stands in for Quasar.Host: logs each call next to itself, `run` sleeps.
    private string FakeHost(string directory, string version = "1")
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "Quasar.Host");
        File.WriteAllText(path + ".tmp", $"#!/bin/sh\n# {version}\necho \"$$ $* stopunit=${{QUASAR_HOST_STOP_UNIT-unset}}\" >> \"$(dirname \"$0\")/calls.log\"\n"
            + "case \"$1\" in run) exec sleep 300;; esac\n");
        File.SetUnixFileMode(path + ".tmp", (UnixFileMode)0x1c0);
        File.Move(path + ".tmp", path, true);
        return path;
    }

    private static async Task<string[]> WaitForCallsAsync(string binary, int count)
    {
        string log = Path.Combine(Path.GetDirectoryName(binary)!, "calls.log");
        for (int i = 0; i < 100; i++)
        {
            if (File.Exists(log) && File.ReadAllLines(log) is { } lines && lines.Length >= count) return lines;
            await Task.Delay(50);
        }
        throw new TimeoutException("The fake Host was not called.");
    }

    [Fact]
    public async Task LocalHostReportsMissingBinaryWithoutRegistering()
    {
        if (!OperatingSystem.IsLinux()) return;
        var local = LocalHost(Path.Combine(root, "missing-host"), Path.Combine(root, "install"));
        await local.EnsureRunningAsync(default);
        Assert.Empty(hosts.GetAll());
        Assert.Contains(ClusterHostInstaller.BinaryVariable, local.Error);
        Assert.Equal("Local Host executor is not available.", local.Status);
        Assert.False(Directory.Exists(Path.Combine(root, "install", "LocalHost")));
    }

    [Fact]
    public void LocalHostPrefersAPrivateAddressForMultiHostClusters()
    {
        Assert.Equal("192.168.1.10", ClusterLocalHost.SelectLocalAddress(
            [IPAddress.Loopback, IPAddress.Parse("8.8.8.8"), IPAddress.Parse("fd00::10"), IPAddress.Parse("192.168.1.10")]));
        Assert.Equal("fd00::10", ClusterLocalHost.SelectLocalAddress([IPAddress.Parse("fd00::10")]));
        Assert.Equal("127.0.0.1", ClusterLocalHost.SelectLocalAddress([IPAddress.Loopback, IPAddress.Parse("8.8.8.8")]));
    }

    [Fact]
    public void LocalHostCommandPortDependsOnTheInstallAndSkipsUsedPorts()
    {
        int first = ClusterLocalHost.SelectCommandPort("/home/a/Quasar", [], _ => true);
        Assert.InRange(first, 18401, 19400);
        Assert.Equal(first, ClusterLocalHost.SelectCommandPort("/home/a/Quasar", [], _ => true));
        Assert.NotEqual(first, ClusterLocalHost.SelectCommandPort("/home/a/Quasar-test", [], _ => true));
        Assert.Equal(first + 2, ClusterLocalHost.SelectCommandPort("/home/a/Quasar", [first], port => port != first + 1));
    }

    [Fact]
    public async Task LocalHostRunsFromTheReleaseWithEverythingInTheInstallRoot()
    {
        if (!OperatingSystem.IsLinux()) return;
        string install = Path.Combine(root, "install");
        string binary = FakeHost(Path.Combine(root, "release-1"));
        var local = LocalHost(binary, install);
        try
        {
            await local.EnsureRunningAsync(default);
            Assert.Null(local.Error);
            var host = Assert.Single(hosts.GetAll());
            Assert.Equal(ClusterLocalHost.HostId, host.Id);
            Assert.EndsWith("(this PC)", host.Name);
            string folder = Path.Combine(install, "LocalHost");
            // The output pump creates host.log asynchronously after process startup.
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                while (!File.Exists(Path.Combine(folder, "host.log"))) await Task.Delay(10, deadline.Token);
            Assert.Equal(new[] { "host.json", "host.log", "host.pid", "state", "stop-clusters" },
                Directory.EnumerateFileSystemEntries(folder).Select(Path.GetFileName).Order().ToArray());
            using (var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "host.json"))))
            {
                Assert.Equal("local", config.RootElement.GetProperty("hostId").GetString());
                Assert.Equal("http://127.0.0.1:" + host.CommandPort, config.RootElement.GetProperty("command").GetProperty("url").GetString());
                Assert.Equal("http://127.0.0.1:8080/", config.RootElement.GetProperty("connection").GetProperty("quasarUrl").GetString());
            }
            string secrets = Path.Combine(folder, "state", "credentials.json");
            Assert.Equal(credentials.Resolve(host.CredentialReference), JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(secrets))![host.CredentialReference]);
            Assert.Equal((UnixFileMode)0x180, File.GetUnixFileMode(secrets));
            Assert.Equal((UnixFileMode)0x1c0, File.GetUnixFileMode(folder));
            Assert.Contains($"'{binary}' stop-clusters --config '{Path.Combine(folder, "host.json")}'", File.ReadAllText(local.StopScriptPath));

            string call = Assert.Single(await WaitForCallsAsync(binary, 1));
            var running = local.ReadPid()!;
            Assert.Equal($"{running.Pid} run --config {Path.Combine(folder, "host.json")} stopunit=", call);
            // Its own session, so a Ctrl+C on a foreground Quasar doesn't reach it.
            string stat = File.ReadAllText($"/proc/{running.Pid}/stat");
            Assert.Equal(running.Pid.ToString(), stat[(stat.LastIndexOf(')') + 2)..].Split(' ')[3]);

            // A new worker adopts the running Host instead of starting another one.
            var restarted = LocalHost(binary, install);
            await restarted.EnsureRunningAsync(default);
            Assert.Equal(running, restarted.ReadPid());
            Assert.Single(await WaitForCallsAsync(binary, 1));

            // After a Quasar update the Host restarts from the new release.
            string updated = FakeHost(Path.Combine(root, "release-2"), "2");
            var afterUpdate = LocalHost(updated, install);
            await afterUpdate.EnsureRunningAsync(default);
            var next = afterUpdate.ReadPid()!;
            Assert.NotEqual(running.Pid, next.Pid);
            Assert.Null(ClusterLocalHost.ProcessIdentity(running.Pid));
            Assert.Equal(updated, next.Binary);
            Assert.Contains(updated, File.ReadAllText(afterUpdate.StopScriptPath));

            // No cluster on this PC: the Host stops with Quasar and nothing is left running.
            await afterUpdate.StopAsync(default);
            Assert.Null(ClusterLocalHost.ProcessIdentity(next.Pid));
            Assert.False(File.Exists(Path.Combine(folder, "host.pid")));
            Assert.Equal(new[] { "LocalHost" }, Directory.EnumerateFileSystemEntries(install).Select(Path.GetFileName).ToArray());
        }
        finally
        {
            foreach (var record in new[] { local.ReadPid() }.OfType<ClusterLocalHost.HostPid>()) Process(record.Pid)?.Kill();
        }
    }

    private static System.Diagnostics.Process? Process(int pid)
    {
        try { return System.Diagnostics.Process.GetProcessById(pid); } catch (ArgumentException) { return null; }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LocalHostKeepsItsRunningClustersOnlyWhenQuasarPreservesServers(bool preserve)
    {
        if (!OperatingSystem.IsLinux()) return;
        string install = Path.Combine(root, "install");
        string binary = FakeHost(Path.Combine(root, "release"));
        var local = LocalHost(binary, install, preserve: preserve);
        await local.EnsureRunningAsync(default);
        var running = local.ReadPid()!;
        // A Host stopped before its shell got to log 'run' never logs it, so wait for that line first.
        await WaitForCallsAsync(binary, 1);
        try
        {
            string state = Path.Combine(install, "LocalHost", "state");
            Directory.CreateDirectory(Path.Combine(state, "gateways"));
            Directory.CreateDirectory(Path.Combine(state, "attachments"));
            File.WriteAllText(Path.Combine(state, "attachments", "a.json"), """{"clusterId":"alpha"}""");
            File.WriteAllText(Path.Combine(state, "gateways", "a.json"), """{"clusterId":"alpha","goal":"Off"}""");
            Assert.False(local.HasRunningClusters());
            File.WriteAllText(Path.Combine(state, "attachments", "b.json"), """{"clusterId":"beta"}""");
            Assert.True(local.HasRunningClusters()); // beta's Gateway runs on another machine
            File.Delete(Path.Combine(state, "attachments", "b.json"));
            File.WriteAllText(Path.Combine(state, "gateways", "a.json"), """{"clusterId":"alpha","goal":"On"}""");
            Assert.True(local.HasRunningClusters());

            await local.StopAsync(default);
            if (preserve)
                Assert.Equal(running, local.ReadPid());
            else
            {
                Assert.Null(ClusterLocalHost.ProcessIdentity(running.Pid));
                Assert.EndsWith(" stop-clusters --config " + Path.Combine(install, "LocalHost", "host.json") + " stopunit=unset",
                    (await WaitForCallsAsync(binary, 2))[1]);
            }
        }
        finally { Process(running.Pid)?.Kill(); }
    }

    [Fact]
    public async Task SecondInstallGetsItsOwnLocalHost()
    {
        if (!OperatingSystem.IsLinux()) return;
        string binary = FakeHost(Path.Combine(root, "release"));
        var otherCredentials = new ClusterCredentialStore(new EphemeralDataProtectionProvider(), Path.Combine(root, "second", "cluster-credentials.json"));
        var otherHosts = new ClusterHostCatalog(otherCredentials, Path.Combine(root, "second", "Hosts"));
        var first = LocalHost(binary, Path.Combine(root, "first"));
        var second = LocalHost(binary, Path.Combine(root, "second"), otherHosts, otherCredentials);
        try
        {
            await first.EnsureRunningAsync(default);
            await second.EnsureRunningAsync(default);
            Assert.NotEqual(first.ReadPid()!.Pid, second.ReadPid()!.Pid);
            var a = hosts.Get(ClusterLocalHost.HostId)!; var b = otherHosts.Get(ClusterLocalHost.HostId)!;
            Assert.NotEqual(a.CommandPort, b.CommandPort);
            Assert.NotEqual(credentials.Resolve(a.CredentialReference), otherCredentials.Resolve(b.CredentialReference));
            await first.StopAsync(default);
            Assert.NotNull(second.ReadPid());
        }
        finally
        {
            await first.StopAsync(default);
            await second.StopAsync(default);
        }
    }

    [Fact]
    public async Task UninstallSavesTheClustersThenStopsOnlyThisInstallsLocalHost()
    {
        if (!OperatingSystem.IsLinux()) return;
        string home = Path.Combine(root, "home"), fakeBin = Path.Combine(root, "fake-bin"), units = Path.Combine(home, ".config/systemd/user");
        Directory.CreateDirectory(units);
        Directory.CreateDirectory(fakeBin);
        File.WriteAllText(Path.Combine(fakeBin, "systemctl"), "#!/bin/sh\necho \"$*\" >> \"$HOME/systemctl.log\"\n");
        File.SetUnixFileMode(Path.Combine(fakeBin, "systemctl"), (UnixFileMode)0x1c0);
        async Task<string> Uninstall(params string[] args)
        {
            var start = new System.Diagnostics.ProcessStartInfo("bash") { RedirectStandardOutput = true, UseShellExecute = false };
            start.ArgumentList.Add(Path.Combine(Path.GetDirectoryName(SourcePath())!, "..", "uninstall.sh"));
            foreach (string arg in args) start.ArgumentList.Add(arg);
            start.Environment["HOME"] = home;
            start.Environment["PATH"] = fakeBin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
            using var process = System.Diagnostics.Process.Start(start)!;
            string output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);
            return output;
        }
        string binary = FakeHost(Path.Combine(root, "release"));
        string q1 = Path.Combine(root, "q1"), q2 = Path.Combine(root, "q2");
        var first = LocalHost(binary, q1);
        var secondCredentials = new ClusterCredentialStore(new EphemeralDataProtectionProvider(), Path.Combine(q2, "cluster-credentials.json"));
        var second = LocalHost(binary, q2, new ClusterHostCatalog(secondCredentials, Path.Combine(q2, "Hosts")), secondCredentials);
        try
        {
            await first.EnsureRunningAsync(default);
            await second.EnsureRunningAsync(default);
            await WaitForCallsAsync(binary, 2); // both 'run' lines, before the uninstalls stop the Hosts
            var firstPid = first.ReadPid()!;
            File.WriteAllText(Path.Combine(units, "quasar.service"), "");
            File.WriteAllText(Path.Combine(units, "quasar-clusters.service"), "");
            // An older version's home-folder Host that q1 enrolled.
            await new ClusterHostCatalog(credentials, Path.Combine(q1, "Hosts")).RegisterAsync("old", "old", "127.0.0.1", 18400, default);
            File.WriteAllText(Path.Combine(units, "quasar-host-old.service"), "");
            Directory.CreateDirectory(Path.Combine(home, ".local/share/Quasar/Hosts/old"));

            string output = await Uninstall("--install-dir", q1);
            string[] calls = File.ReadAllLines(Path.Combine(home, "systemctl.log"));
            // The clusters unit's ExecStop saves the clusters through the running Host, so it goes first.
            Assert.True(Array.IndexOf(calls, "--user disable --now quasar-clusters.service") is >= 0 and var saved
                && saved < Array.IndexOf(calls, "--user stop quasar.service"));
            Assert.Null(ClusterLocalHost.ProcessIdentity(firstPid.Pid));
            Assert.False(File.Exists(Path.Combine(q1, "LocalHost", "host.pid")));
            Assert.True(File.Exists(Path.Combine(q1, "LocalHost", "host.json")));
            Assert.False(File.Exists(Path.Combine(units, "quasar-clusters.service")));
            Assert.Contains("quasar-host-old.service was left alone", output);
            Assert.True(File.Exists(Path.Combine(units, "quasar-host-old.service")));
            Assert.NotNull(second.ReadPid());

            // Without a clusters unit (an install from before it existed) the clusters are saved directly.
            var secondPid = second.ReadPid()!;
            await Uninstall("--install-dir", q2, "--service-name", "quasar-two", "--purge");
            Assert.Contains((await WaitForCallsAsync(binary, 3)).Skip(2), line => line.Contains(" stop-clusters --config " + Path.Combine(q2, "LocalHost", "host.json")));
            Assert.Null(ClusterLocalHost.ProcessIdentity(secondPid.Pid));
            Assert.False(Directory.Exists(q2));
        }
        finally
        {
            await first.StopAsync(default);
            await second.StopAsync(default);
        }
    }

    private static string SourcePath([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;

    [Fact]
    public async Task ManualEnrollmentPackageContainsTheMatchingPrivateHostIdentityWithoutSystemd()
    {
        var host = await hosts.RegisterAsync("one", "One", "10.0.0.1", 18400, default);
        string binary = Path.Combine(root, "Quasar.Host");
        await File.WriteAllTextAsync(binary, "host binary");
        var installer = new ClusterHostInstaller(hosts, credentials, binary, TimeProvider.System);
        using var archive = new System.IO.Compression.ZipArchive(new MemoryStream(installer.CreateManualPackage(host.Id, "https://quasar.example.com")));
        string Read(string name)
        {
            using var reader = new StreamReader(archive.GetEntry(name)!.Open());
            return reader.ReadToEnd();
        }
        using var config = JsonDocument.Parse(Read("host.json"));
        Assert.Equal(host.Id, config.RootElement.GetProperty("hostId").GetString());
        Assert.Equal("http://127.0.0.1:18400", config.RootElement.GetProperty("command").GetProperty("url").GetString());
        Assert.Equal("https://quasar.example.com/", config.RootElement.GetProperty("connection").GetProperty("quasarUrl").GetString());
        Assert.Equal(credentials.Resolve(host.CredentialReference), JsonSerializer.Deserialize<Dictionary<string, string>>(Read("state/credentials.json"))![host.CredentialReference]);
        Assert.Equal("host binary", Read("Quasar.Host"));
        Assert.Equal(0x180, (archive.GetEntry("state/credentials.json")!.ExternalAttributes >> 16) & 0x1ff);
        Assert.Contains("./Quasar.Host run --config host.json", Read("README.txt"));
        Assert.Throws<ArgumentException>(() => installer.CreateManualPackage(host.Id, "http://remote.example.com"));
        Assert.Throws<KeyNotFoundException>(() => installer.CreateManualPackage("unknown", "https://quasar.example.com"));
    }

    [Theory]
    [InlineData("http://remote.example.com")]
    [InlineData("https://user:pass@example.com")]
    [InlineData("https://example.com/path")]
    [InlineData("https://example.com?token=secret")]
    public void EnrollmentRejectsUnsafeOrigins(string origin) =>
        Assert.Throws<ArgumentException>(() => ClusterHostInstaller.ValidateOrigin(origin));

    [Fact]
    public async Task CredentialsPersistEncryptedAndAuthenticateOnlyTheirHost()
    {
        var host = await hosts.RegisterAsync("one", "One", "10.0.0.1", 18400, default);
        var other = await hosts.RegisterAsync("two", "Two", "10.0.0.2", 18400, default);
        string secret = credentials.Resolve(host.CredentialReference)!;
        Assert.DoesNotContain(secret, File.ReadAllText(Path.Combine(root, "credentials.json")));
        Assert.True(hosts.Authenticate(host.Id, "Bearer " + secret));
        Assert.False(hosts.Authenticate(other.Id, "Bearer " + secret));
        Assert.False(hosts.Authenticate(host.Id, null));
        Assert.False(hosts.Authenticate(host.Id, "Bearer " + secret[..^1]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => hosts.RegisterAsync("one", "Other", "10.0.0.2", 18400, default));
        Assert.Equal(host, new ClusterHostCatalog(credentials, Path.Combine(root, "hosts")).Get(host.Id));
    }

    [Fact]
    public void HostBinaryDefaultsToTheReleaseLayoutAndHonoursTheDevelopmentOverride()
    {
        Assert.Equal(Path.Combine(root, "Host", "Quasar.Host"), ClusterHostInstaller.ResolveBinary(null, root));
        Assert.Equal(Path.Combine(root, "Host", "Quasar.Host"), ClusterHostInstaller.ResolveBinary(" ", root));
        Assert.Equal(Path.Combine(root, "dev", "Quasar.Host"), ClusterHostInstaller.ResolveBinary(Path.Combine(root, "dev", "Quasar.Host"), "/elsewhere"));
    }

    [Fact]
    public async Task CorruptHostRegistrationDoesNotBreakOtherHosts()
    {
        var healthy = await hosts.RegisterAsync("one", "One", "10.0.0.1", 18400, default);
        await hosts.RegisterAsync("two", "Two", "10.0.0.2", 18400, default);
        File.WriteAllBytes(Path.Combine(root, "hosts", "two", "host.json"), []);

        Assert.Equal([healthy], hosts.GetAll());
        Assert.True(hosts.Authenticate(healthy.Id, "Bearer " + credentials.Resolve(healthy.CredentialReference)));
        Assert.False(hosts.Authenticate("two", "Bearer anything"));
        // The broken registration can be replaced by enrolling the Host again.
        Assert.Equal("two", (await hosts.RegisterAsync("two", "Two", "10.0.0.2", 18400, default)).Id);
    }

    [Fact]
    public void CorruptCredentialFileIsSetAsideInsteadOfAbortingStartup()
    {
        string path = Path.Combine(root, "torn-credentials.json");
        File.WriteAllText(path, "{\"QSR_MANAGED_");

        var store = new ClusterCredentialStore(new EphemeralDataProtectionProvider(), path);

        Assert.Single(Directory.GetFiles(root, "torn-credentials.json.corrupt-*"));
        string reference = store.Create("cluster:demo", "admin");
        Assert.NotNull(store.Resolve(reference));
        Assert.Single(Directory.GetFiles(root, "torn-credentials.json.corrupt-*"));
    }

    [Fact]
    public async Task InstallTicketsExpireAndRejectReplayAndReplacedTickets()
    {
        var host = await hosts.RegisterAsync("one", "One", "10.0.0.1", 18400, default);
        string binary = Path.Combine(root, "Quasar.Host"); File.WriteAllText(binary, "test executable");
        var clock = new TestClock();
        var installer = new ClusterHostInstaller(hosts, credentials, binary, clock);
        (Guid Id, string Auth) Decode(HostInstallTicket ticket) =>
            (Guid.Parse(Regex.Match(ticket.Command, "host-enrollment/([a-f0-9]+)").Groups[1].Value),
             "Bearer " + Regex.Match(ticket.Command, "Bearer ([A-F0-9]+)").Groups[1].Value);
        var first = Decode(installer.Issue(host.Id, "https://quasar.example.com"));
        Assert.Null(installer.Redeem(first.Id, "Bearer wrong"));
        var script = Encoding.UTF8.GetString(installer.Redeem(first.Id, first.Auth)!);
        Assert.Contains("systemctl --user enable --now", script);
        Assert.Contains("quasar-host-one-update.timer", script);
        Assert.Equal(1, Regex.Matches(script, "cat <<'QSR_BINARY'").Count);
        Assert.Contains(Regex.Matches(script, "printf '%s' '([^']+)' ").Select(match =>
            Encoding.UTF8.GetString(Convert.FromBase64String(match.Groups[1].Value))),
            value => value.Contains("http://127.0.0.1:18400"));
        Assert.Null(installer.Redeem(first.Id, first.Auth));
        var second = Decode(installer.Issue(host.Id, "https://quasar.example.com"));
        var third = Decode(installer.Issue(host.Id, "https://quasar.example.com"));
        Assert.Null(installer.Redeem(second.Id, second.Auth));
        clock.Now += TimeSpan.FromMinutes(15);
        Assert.Null(installer.Redeem(third.Id, third.Auth));
        await Assert.ThrowsAsync<ArgumentException>(() => installer.InstallSshAsync(host.Id, "http://localhost:8080", new("10.0.0.1", "user"), default));
        string file = Path.Combine(root, "install.sh"); File.WriteAllText(file, script);
        using var syntax = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("bash")
        { ArgumentList = { "-n", file }, UseShellExecute = false })!;
        await syntax.WaitForExitAsync(); Assert.Equal(0, syntax.ExitCode);
    }

    [Fact]
    public async Task ExistingHostUpgradeKeepsStateAndRollsBackFailedRestart()
    {
        if (!OperatingSystem.IsLinux()) return;
        var host = await hosts.RegisterAsync("one", "One", "127.0.0.1", 18400, default);
        string binary = Path.Combine(root, "Quasar.Host");
        string home = Path.Combine(root, "home");
        string fakeBin = Path.Combine(root, "fake-bin");
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(fakeBin);
        string systemctl = Path.Combine(fakeBin, "systemctl");
        File.WriteAllText(systemctl, """
            #!/bin/sh
            echo "$*" >> "$HOME/systemctl.log"
            if [ "$2" = restart ] && [ -e "$HOME/fail-restart" ]; then
                rm "$HOME/fail-restart"
                exit 1
            fi
            exit 0
            """ + "\n");
        File.SetUnixFileMode(systemctl, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        // Never change the lingering setting of the machine running the tests.
        foreach (string tool in new[] { "loginctl", "sudo" })
        {
            File.WriteAllText(Path.Combine(fakeBin, tool), $"#!/bin/sh\necho \"$*\" >> \"$HOME/{tool}.log\"\nexit 1\n");
            File.SetUnixFileMode(Path.Combine(fakeBin, tool), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var installer = new ClusterHostInstaller(hosts, credentials, binary, TimeProvider.System);
        string installed = Path.Combine(home, ".local/share/Quasar/Hosts/one");

        // Returns the installer's error, or null on success. A rejected install exits before reading the
        // whole script, so writing it directly could fail with a broken pipe; RunAsync tolerates that.
        async Task<string?> Install(string version, string origin)
        {
            File.WriteAllText(binary, version);
            byte[] script = installer.BuildScript(host, new Uri(origin));
            var start = new System.Diagnostics.ProcessStartInfo("bash");
            start.Environment["HOME"] = home;
            start.Environment["PATH"] = fakeBin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
            try { await ClusterHostInstaller.RunAsync(start, script, default); }
            catch (InvalidOperationException error) { return error.Message; }
            return null;
        }

        Assert.Null(await Install("old", "http://localhost:8080"));
        string configPath = Path.Combine(installed, "host.json");
        string config = File.ReadAllText(configPath).Replace("\"attachments\":[]", "\"attachments\":[{\"name\":\"existing\"}]");
        File.WriteAllText(configPath, config);
        string credentialPath = Path.Combine(installed, "state/credentials.json");
        var stored = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(credentialPath))!;
        stored["QSR_MANAGED_CLUSTER_EXTRA"] = "retained cluster credential";
        string secret = JsonSerializer.Serialize(stored);
        File.WriteAllText(credentialPath, secret);
        File.WriteAllText(Path.Combine(installed, "state/keep"), "running cluster state");

        Assert.Null(await Install("new", "http://127.0.0.1:8080"));
        Assert.Equal("new", File.ReadAllText(Path.Combine(installed, "Quasar.Host")));
        Assert.Equal(config, File.ReadAllText(configPath));
        Assert.Equal(secret, File.ReadAllText(credentialPath));
        Assert.Equal("running cluster state", File.ReadAllText(Path.Combine(installed, "state/keep")));
        Assert.Equal(1, File.ReadAllLines(Path.Combine(home, "systemctl.log")).Count(line => line.Contains(" restart ")));

        Assert.Null(await Install("new", "http://127.0.0.1:8080"));
        string[] systemctlCalls = File.ReadAllLines(Path.Combine(home, "systemctl.log"));
        Assert.Equal(1, systemctlCalls.Count(line => line.Contains(" restart ")));
        // Stopping the stop unit shuts the clusters down, so updates only ever start it.
        Assert.Contains("--user enable --now quasar-host-one-stop.service", systemctlCalls);
        Assert.DoesNotContain(systemctlCalls, line => line.Contains("stop.service") && !line.Contains("enable --now"));
        string stopUnit = File.ReadAllText(Path.Combine(home, ".config/systemd/user/quasar-host-one-stop.service"));
        Assert.Contains("After=quasar-host-one.service", stopUnit);
        Assert.Contains("Quasar.Host\" stop-clusters --config", stopUnit);

        File.WriteAllText(Path.Combine(home, "fail-restart"), "fail once");
        Assert.NotNull(await Install("broken", "http://127.0.0.1:8080"));
        Assert.Equal("new", File.ReadAllText(Path.Combine(installed, "Quasar.Host")));

        File.WriteAllText(credentialPath, "different enrollment");
        // Larger than any pipe buffer, like a real Host, so the rejected installer always exits before
        // reading its whole input.
        Assert.Contains("enrollment credential differs", await Install("foreign" + new string('x', 1 << 20), "http://127.0.0.1:8080"));
        Assert.Equal("new", File.ReadAllText(Path.Combine(installed, "Quasar.Host")));
    }

    [Fact]
    public async Task InstallerReportsRemoteFailureInsteadOfBrokenPipe()
    {
        var start = new System.Diagnostics.ProcessStartInfo("bash");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("echo 'Existing Host credentials differ' >&2; exit 7");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ClusterHostInstaller.RunAsync(start, new byte[1024 * 1024], default));
        Assert.Contains("Existing Host credentials differ", error.Message);
    }

    [Fact]
    public async Task EnrolledUpdaterDownloadsVerifiedHostAndRollsBackFailedRestart()
    {
        if (!OperatingSystem.IsLinux()) return;
        var host = await hosts.RegisterAsync("one", "One", "127.0.0.1", 18400, default);
        string releaseBinary = Path.Combine(root, "release-host");
        File.WriteAllText(releaseBinary, "verified host release");
        var installer = new ClusterHostInstaller(hosts, credentials, releaseBinary, TimeProvider.System);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton(hosts);
        builder.Services.AddSingleton<ClusterHostTunnels>();
        builder.Services.AddSingleton(installer);
        await using var app = builder.Build();
        app.MapClusterHostEnrollmentApi();
        await app.StartAsync();
        string origin = app.Urls.Single();
        string credential = credentials.Resolve(host.CredentialReference)!;
        using var client = new HttpClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(origin + "/api/v1/hosts/one/update")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(origin + "/api/v1/hosts/one/update/binary")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", credential);
        var metadata = await client.GetFromJsonAsync<HostBinaryInfo>(origin + "/api/v1/hosts/one/update");
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(releaseBinary))).ToLowerInvariant(), metadata!.Sha256);
        Assert.Equal(File.ReadAllBytes(releaseBinary), await client.GetByteArrayAsync(origin + "/api/v1/hosts/one/update/binary"));

        string home = Path.Combine(root, "home");
        string installed = Path.Combine(home, ".local/share/Quasar/Hosts/one");
        string fakeBin = Path.Combine(root, "fake-bin");
        Directory.CreateDirectory(Path.Combine(installed, "state"));
        Directory.CreateDirectory(fakeBin);
        File.WriteAllText(Path.Combine(installed, "Quasar.Host"), "old host");
        File.WriteAllText(Path.Combine(installed, "host.json"), JsonSerializer.Serialize(new
        {
            hostId = host.Id,
            connection = new { quasarUrl = origin + "/", tokenEnvironmentVariable = host.CredentialReference },
        }));
        File.WriteAllText(Path.Combine(installed, "state/credentials.json"),
            JsonSerializer.Serialize(new Dictionary<string, string> { [host.CredentialReference] = credential }));
        string systemctl = Path.Combine(fakeBin, "systemctl");
        File.WriteAllText(systemctl, """
            #!/bin/sh
            if [ "$2" = restart ] && [ -e "$HOME/fail-restart" ]; then exit 1; fi
            if [ "$2" = show ]; then printf 'ActiveState=active\nMainPID=123\n'; fi
            exit 0
            """ + "\n");
        File.SetUnixFileMode(systemctl, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string updater = Path.Combine(root, "host-update.py");
        using (var resource = typeof(ClusterHostInstaller).Assembly.GetManifestResourceStream("Quasar.Assets.host-update.py")!)
        using (var output = File.Create(updater)) await resource.CopyToAsync(output);

        async Task<int> RunUpdater()
        {
            var start = new System.Diagnostics.ProcessStartInfo("python3")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(updater);
            start.ArgumentList.Add(installed);
            start.Environment["HOME"] = home;
            start.Environment["PATH"] = fakeBin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
            using var process = System.Diagnostics.Process.Start(start)!;
            _ = await process.StandardOutput.ReadToEndAsync();
            _ = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return process.ExitCode;
        }

        Assert.Equal(0, await RunUpdater());
        Assert.Equal("verified host release", File.ReadAllText(Path.Combine(installed, "Quasar.Host")));
        File.WriteAllText(releaseBinary, "broken new release");
        File.WriteAllText(Path.Combine(home, "fail-restart"), "fail");
        Assert.NotEqual(0, await RunUpdater());
        Assert.Equal("verified host release", File.ReadAllText(Path.Combine(installed, "Quasar.Host")));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(releaseBinary))).ToLowerInvariant(),
            File.ReadAllText(Path.Combine(installed, "state/failed-host-update-sha256")));
        Assert.NotEqual(0, await RunUpdater());
        await app.StopAsync();
    }

    [Fact]
    public async Task ConnectedHostDoesNotHoldGracefulShutdown()
    {
        var host = await hosts.RegisterAsync("one", "One", "10.0.0.1", 18400, default);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton(hosts); builder.Services.AddSingleton<ClusterHostTunnels>();
        builder.Services.AddSingleton(new ClusterHostInstaller(hosts, credentials, Path.Combine(root, "Quasar.Host"), TimeProvider.System));
        await using var app = builder.Build();
        app.UseWebSockets(); app.MapClusterHostEnrollmentApi();
        await app.StartAsync();
        using var control = new ClientWebSocket();
        control.Options.SetRequestHeader("Authorization", "Bearer " + credentials.Resolve(host.CredentialReference));
        var url = new UriBuilder(new Uri(new Uri(app.Urls.Single()), "/api/v1/hosts/one/connect")) { Scheme = "ws" }.Uri;
        await control.ConnectAsync(url, default);
        // The server registers the channel after the handshake the client already saw complete.
        var tunnels = app.Services.GetRequiredService<ClusterHostTunnels>();
        for (int i = 0; i < 100 && !tunnels.IsConnected("one"); i++) await Task.Delay(50);
        Assert.True(tunnels.IsConnected("one"));

        // The Host keeps its control channel open for as long as it runs; shutdown must not wait for it.
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await app.StopAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"StopAsync took {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task HttpCommandsStreamThroughAuthenticatedHostBoundTunnel()
    {
        var host = await hosts.RegisterAsync("one", "One", "10.0.0.1", 18400, default);
        var other = await hosts.RegisterAsync("two", "Two", "10.0.0.2", 18400, default);
        var tunnels = new ClusterHostTunnels();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton(hosts); builder.Services.AddSingleton(tunnels);
        builder.Services.AddSingleton(new ClusterHostInstaller(hosts, credentials, Path.Combine(root, "Quasar.Host"), TimeProvider.System));
        await using var app = builder.Build();
        app.UseWebSockets(); app.MapClusterHostEnrollmentApi();
        await app.StartAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var origin = new Uri(app.Urls.Single());
        Uri Url(string path) => new UriBuilder(new Uri(origin, path)) { Scheme = "ws" }.Uri;
        using var unauthenticated = new ClientWebSocket();
        await Assert.ThrowsAsync<WebSocketException>(() => unauthenticated.ConnectAsync(Url("/api/v1/hosts/one/connect"), deadline.Token));
        using var control = new ClientWebSocket();
        control.Options.SetRequestHeader("Authorization", "Bearer " + credentials.Resolve(host.CredentialReference));
        await control.ConnectAsync(Url("/api/v1/hosts/one/connect"), deadline.Token);
        using var http = new HttpClient(new SocketsHttpHandler { ConnectCallback = tunnels.OpenAsync, UseProxy = false });
        var request = new HttpRequestMessage(HttpMethod.Get, host.CommandUrl + "/host/v1/status");
        request.Headers.Host = "127.0.0.1:18400";
        var responseTask = http.SendAsync(request, deadline.Token);
        byte[] signal = new byte[64]; var message = await control.ReceiveAsync(signal.AsMemory(), deadline.Token);
        string id = Encoding.UTF8.GetString(signal, 0, message.Count);
        using var wrongHost = new ClientWebSocket();
        wrongHost.Options.SetRequestHeader("Authorization", "Bearer " + credentials.Resolve(other.CredentialReference));
        await Assert.ThrowsAsync<WebSocketException>(() => wrongHost.ConnectAsync(Url("/api/v1/hosts/two/tunnels/" + id), deadline.Token));
        using var tunnel = new ClientWebSocket();
        tunnel.Options.SetRequestHeader("Authorization", "Bearer " + credentials.Resolve(host.CredentialReference));
        await tunnel.ConnectAsync(Url("/api/v1/hosts/one/tunnels/" + id), deadline.Token);
        byte[] requestBytes = new byte[4096]; var received = await tunnel.ReceiveAsync(requestBytes.AsMemory(), deadline.Token);
        Assert.Contains("Host: 127.0.0.1:18400", Encoding.ASCII.GetString(requestBytes, 0, received.Count));
        string body = new('x', 180000);
        await tunnel.SendAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"), WebSocketMessageType.Binary, true, deadline.Token);
        foreach (var offset in Enumerable.Range(0, 6))
            await tunnel.SendAsync(Encoding.ASCII.GetBytes(body.Substring(offset * 30000, 30000)), WebSocketMessageType.Binary, true, deadline.Token);
        using var response = await responseTask;
        Assert.Equal(body, await response.Content.ReadAsStringAsync(deadline.Token));
        await control.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", deadline.Token);
        while (tunnels.IsConnected(host.Id)) await Task.Delay(10, deadline.Token);
        await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync(host.CommandUrl + "/host/v1/status", deadline.Token));
        await app.StopAsync(deadline.Token);
    }
    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
