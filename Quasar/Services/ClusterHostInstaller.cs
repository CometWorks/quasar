using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Quasar.Services;

public sealed record HostInstallTicket(string Command, DateTimeOffset ExpiresAt);
public sealed record HostSshInstall(string Address, string User, int Port = 22, string? IdentityFile = null);

public sealed class ClusterHostInstaller
{
    private readonly ClusterHostCatalog hosts;
    private readonly ClusterCredentialStore credentials;
    private readonly string binary;
    private readonly TimeProvider clock;
    public ClusterHostInstaller(ClusterHostCatalog hosts, ClusterCredentialStore credentials)
        : this(hosts, credentials, ResolveBinary(Environment.GetEnvironmentVariable(BinaryVariable), AppContext.BaseDirectory), TimeProvider.System) { }
    // Releases ship the single-file Host next to the web worker. Plain build output (dotnet run)
    // has none, so development points this variable at a published single-file Quasar.Host.
    internal const string BinaryVariable = "QUASAR_HOST_BINARY";
    internal static string ResolveBinary(string? configured, string baseDirectory) => string.IsNullOrWhiteSpace(configured)
        ? Path.Combine(baseDirectory, "Host", "Quasar.Host") : Path.GetFullPath(configured);
    internal ClusterHostInstaller(ClusterHostCatalog hosts, ClusterCredentialStore credentials, string binary, TimeProvider clock)
        => (this.hosts, this.credentials, this.binary, this.clock) = (hosts, credentials, binary, clock);
    private sealed record Ticket(string Hash, string Host, Uri Origin, DateTimeOffset Expires);
    private readonly ConcurrentDictionary<Guid, Ticket> _tickets = new();
    public HostInstallTicket Issue(string hostId, string quasarUrl)
    {
        var origin = ValidateOrigin(quasarUrl);
        _ = hosts.Get(hostId) ?? throw new KeyNotFoundException("Machine is not registered.");
        if (!File.Exists(binary)) throw new InvalidOperationException($"This Quasar build does not include the Host installer ({binary}). Install a release containing Host/Quasar.Host, or for a development build set {BinaryVariable} to a published single-file Quasar.Host.");
        var id = Guid.NewGuid(); string secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var expires = clock.GetUtcNow().AddMinutes(15);
        foreach (var old in _tickets.Where(p => p.Value.Expires <= clock.GetUtcNow() || p.Value.Host == hostId)) _tickets.TryRemove(old.Key, out _);
        _tickets[id] = new(Hash(secret), hostId, origin, expires);
        string url = new Uri(origin, "/api/v1/host-enrollment/" + id.ToString("N")).AbsoluteUri;
        // Download completes before execution; a truncated response never runs as a shell script.
        string command = $"(set -e; qsr_script=$(mktemp); trap 'rm -f \"$qsr_script\"' EXIT; curl --fail --silent --show-error --header {Quote("Authorization: Bearer " + secret)} {Quote(url)} --output \"$qsr_script\"; bash \"$qsr_script\")";
        return new(command, expires);
    }
    internal byte[]? Redeem(Guid id, string? authorization)
    {
        if (!_tickets.TryGetValue(id, out var ticket) || ticket.Expires <= clock.GetUtcNow()
            || authorization is null || !authorization.StartsWith("Bearer ", StringComparison.Ordinal)
            || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(ticket.Hash), Convert.FromHexString(Hash(authorization[7..])))) return null;
        var script = BuildScript(hosts.Get(ticket.Host) ?? throw new KeyNotFoundException(), ticket.Origin);
        return _tickets.TryRemove(new KeyValuePair<Guid, Ticket>(id, ticket)) ? script : null;
    }
    public Task InstallLocalAsync(string hostId, string quasarUrl, CancellationToken token) =>
        RunAsync(new ProcessStartInfo("bash"), BuildScript(hosts.Get(hostId) ?? throw new KeyNotFoundException(), ValidateOrigin(quasarUrl)), token);

    public Task InstallSshAsync(string hostId, string quasarUrl, HostSshInstall request, CancellationToken token)
    {
        var origin = ValidateOrigin(quasarUrl);
        if (origin.IsLoopback) throw new ArgumentException("An SSH-installed Host needs a Quasar HTTPS address reachable from that machine; localhost points to the remote machine itself.");
        if (!Regex.IsMatch(request.User, "^[a-zA-Z_][a-zA-Z0-9_-]{0,63}$")
            || Uri.CheckHostName(request.Address) == UriHostNameType.Unknown || request.Port is < 1 or > 65535)
            throw new ArgumentException("SSH requires a valid machine address, login name and port.");
        var start = new ProcessStartInfo("ssh");
        foreach (string arg in new[] { "-T", "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=yes", "-o", "ConnectTimeout=20", "-p", request.Port.ToString() }) start.ArgumentList.Add(arg);
        if (!string.IsNullOrWhiteSpace(request.IdentityFile))
        {
            if (!Path.IsPathFullyQualified(request.IdentityFile) || !File.Exists(request.IdentityFile)) throw new ArgumentException("SSH identity file must exist on the Quasar machine.");
            start.ArgumentList.Add("-i"); start.ArgumentList.Add(request.IdentityFile);
        }
        start.ArgumentList.Add(request.User + "@" + request.Address);
        start.ArgumentList.Add("bash -s");
        return RunAsync(start, BuildScript(hosts.Get(hostId) ?? throw new KeyNotFoundException(), origin), token);
    }

    internal byte[] BuildScript(EnrolledClusterHost host, Uri origin)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Guided cluster setup currently requires Linux x64.");
        if (!File.Exists(binary)) throw new InvalidOperationException($"This Quasar build does not include the Host installer ({binary}). Install a release containing Host/Quasar.Host, or for a development build set {BinaryVariable} to a published single-file Quasar.Host.");
        var config = new { executorId = "quasar-" + host.Id, hostId = host.Id, pollIntervalSeconds = 2, attachments = Array.Empty<object>(), stateDirectory = "state",
            command = new { url = "http://127.0.0.1:" + host.CommandPort, tokenEnvironmentVariable = host.CredentialReference },
            connection = new { quasarUrl = origin.AbsoluteUri, tokenEnvironmentVariable = host.CredentialReference } };
        string Config(object value) => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(value));
        string payload = Convert.ToBase64String(File.ReadAllBytes(binary), Base64FormattingOptions.InsertLineBreaks).Replace("\r", "");
        string secret = Config(new Dictionary<string, string> { [host.CredentialReference] = credentials.Resolve(host.CredentialReference)! });
        return Encoding.UTF8.GetBytes($$"""
            #!/usr/bin/env bash
            set -euo pipefail
            umask 077
            test "$(uname -s)" = Linux && test "$(uname -m)" = x86_64 || { echo 'A Linux x64 machine is required.' >&2; exit 1; }
            command -v python3 >/dev/null || { echo 'Install Python 3 before enrolling this machine.' >&2; exit 1; }
            command -v dotnet >/dev/null && dotnet --list-runtimes | grep -q '^Microsoft.NETCore.App 10\.' || { echo 'Install the .NET 10 runtime before enrolling this machine. Gateway and world tools require it.' >&2; exit 1; }
            systemctl --user show-environment >/dev/null || { echo 'A working systemd user session is required.' >&2; exit 1; }
            qsr_root="$HOME/.local/share/Quasar/Hosts/{{host.Id}}"
            test ! -L "$qsr_root" || { echo 'The Host directory must not be a symbolic link.' >&2; exit 1; }
            if test -e "$qsr_root"; then
                # Retry only this exact enrollment. Never overwrite another Host or a changed installation.
                printf '%s' '{{Config(config)}}' | base64 --decode | cmp -s - "$qsr_root/host.json" || { echo 'Existing Host configuration differs; its data has been preserved.' >&2; exit 1; }
                printf '%s' '{{secret}}' | base64 --decode | cmp -s - "$qsr_root/state/credentials.json" || { echo 'Existing Host credentials differ; its data has been preserved.' >&2; exit 1; }
                test "$(sha256sum "$qsr_root/Quasar.Host" | cut -d ' ' -f 1)" = '{{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(binary))).ToLowerInvariant()}}' || { echo 'Existing Host binary differs; its data has been preserved.' >&2; exit 1; }
            else
            mkdir -p "$(dirname "$qsr_root")" "$HOME/.config/systemd/user"
            qsr_stage=$(mktemp -d "$(dirname "$qsr_root")/.enroll-XXXXXX")
            trap 'rm -rf "$qsr_stage"' EXIT
            mkdir "$qsr_stage/state"
            cat <<'QSR_BINARY' | base64 --decode > "$qsr_stage/Quasar.Host"
            {{payload}}
            QSR_BINARY
            chmod 700 "$qsr_stage/Quasar.Host"
            printf '%s' '{{Config(config)}}' | base64 --decode > "$qsr_stage/host.json"
            printf '%s' '{{secret}}' | base64 --decode > "$qsr_stage/state/credentials.json"
            chmod 600 "$qsr_stage/host.json" "$qsr_stage/state/credentials.json"
            mv -T "$qsr_stage" "$qsr_root"
            fi
            mkdir -p "$qsr_root/bin"
            ln -sfn "$(command -v dotnet)" "$qsr_root/bin/dotnet"
            cat > "$HOME/.config/systemd/user/quasar-host-{{host.Id}}.service" <<'QSR_UNIT'
            [Unit]
            Description=Quasar Host {{host.Id}}
            After=network-online.target
            [Service]
            Environment="PATH=%h/.local/share/Quasar/Hosts/{{host.Id}}/bin:/usr/local/bin:/usr/bin:/bin"
            ExecStart="%h/.local/share/Quasar/Hosts/{{host.Id}}/Quasar.Host" run --config "%h/.local/share/Quasar/Hosts/{{host.Id}}/host.json"
            Restart=on-failure
            RestartSec=5
            KillMode=process
            [Install]
            WantedBy=default.target
            QSR_UNIT
            systemctl --user daemon-reload
            systemctl --user enable --now quasar-host-{{host.Id}}.service
            echo 'Quasar Host installed. Enable user lingering if it must run after logout: loginctl enable-linger'
            """);
    }

    internal static Uri ValidateOrigin(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || uri.AbsolutePath != "/" || uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback))
            throw new ArgumentException("Use the Quasar HTTPS origin reachable by the machine, for example https://quasar.example.com. HTTP is permitted only for local loopback setup.");
        return uri;
    }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";
    private static async Task RunAsync(ProcessStartInfo start, byte[] script, CancellationToken token)
    {
        start.UseShellExecute = false; start.RedirectStandardInput = true; start.RedirectStandardOutput = true; start.RedirectStandardError = true;
        using var process = Process.Start(start) ?? throw new IOException("Could not start the Host installer.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromMinutes(10));
        var output = ReadTailAsync(process.StandardOutput, deadline.Token);
        var error = ReadTailAsync(process.StandardError, deadline.Token);
        try
        {
            await process.StandardInput.BaseStream.WriteAsync(script, deadline.Token); process.StandardInput.Close();
            await process.WaitForExitAsync(deadline.Token);
            await output;
            if (process.ExitCode != 0) throw new InvalidOperationException("Host installation failed: " + await error);
        }
        catch { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); } throw; }
    }
    private static async Task<string> ReadTailAsync(StreamReader reader, CancellationToken token)
    {
        var tail = new StringBuilder(); char[] buffer = new char[4096]; int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) != 0)
        {
            tail.Append(buffer, 0, count);
            if (tail.Length > 8192) tail.Remove(0, tail.Length - 8192);
        }
        return tail.ToString();
    }
}
