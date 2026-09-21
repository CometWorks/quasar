using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Magnetar.Protocol.Runtime;

namespace Quasar.Services;

public sealed record EnrolledClusterHost(string Id, string Name, string Address, int CommandPort, string CredentialReference)
{
    public string CommandUrl => $"http://{Id}.quasar-host.invalid:{CommandPort}";
}

public sealed class ClusterHostCatalog
{
    private readonly string _root;
    private readonly ClusterCredentialStore credentials;
    private readonly ILogger<ClusterHostCatalog>? _logger;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _reported = new();
    public ClusterHostCatalog(ClusterCredentialStore credentials, ILogger<ClusterHostCatalog> logger)
        : this(credentials, Path.Combine(MagnetarPaths.GetQuasarDirectory(), "Hosts"), logger) { }
    internal ClusterHostCatalog(ClusterCredentialStore credentials, string root, ILogger<ClusterHostCatalog>? logger = null)
        => (this.credentials, _root, _logger) = (credentials, root, logger);
    private readonly SemaphoreSlim _gate = new(1, 1);
    public IReadOnlyList<EnrolledClusterHost> GetAll() => !Directory.Exists(_root) ? [] :
        Directory.EnumerateFiles(_root, "host.json", SearchOption.AllDirectories)
            .Select(Read).OfType<EnrolledClusterHost>().OrderBy(h => h.Name).ToArray();
    // One unreadable registration must not break tunnel authentication for every other Host.
    private EnrolledClusterHost? Read(string path)
    {
        try
        {
            var host = JsonSerializer.Deserialize<EnrolledClusterHost>(File.ReadAllBytes(path));
            if (host is null || string.IsNullOrEmpty(host.Id) || string.IsNullOrEmpty(host.CredentialReference))
                throw new InvalidDataException("The registration is incomplete.");
            _reported.TryRemove(path, out _);
            return host;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            if (_reported.TryAdd(path, true))
                _logger?.LogError(exception, "Host registration {Path} is unreadable and was ignored; enroll that Host again.", path);
            return null;
        }
    }
    public EnrolledClusterHost? Get(string id) => GetAll().SingleOrDefault(h => h.Id == id);
    public async Task<EnrolledClusterHost> RegisterAsync(string id, string name, string address, int commandPort, CancellationToken token)
    {
        if (string.IsNullOrEmpty(id) || !Regex.IsMatch(id, "^[a-z][a-z0-9-]{0,62}$") || !IsClusterAddress(address)
            || commandPort is < 1024 or > 65535) throw new ArgumentException("Use a lowercase machine ID, a private IP address and a control port from 1024 to 65535.");
        await _gate.WaitAsync(token);
        try
        {
            if (Get(id) is not null) throw new InvalidOperationException("A machine with this ID is already registered.");
            address = System.Net.IPAddress.Parse(address).ToString();
            if (GetAll().Any(h => h.Address == address && h.CommandPort == commandPort))
                throw new InvalidOperationException("This machine address and Host control port are already registered. Resume the existing enrollment.");
            var host = new EnrolledClusterHost(id, string.IsNullOrWhiteSpace(name) ? id : name.Trim(), address, commandPort, credentials.Create("host:" + id, "control"));
            string directory = Path.Combine(_root, id); Directory.CreateDirectory(directory);
            await AtomicFileWriter.WriteTextAsync(Path.Combine(directory, "host.json"), JsonSerializer.Serialize(host), token);
            return host;
        }
        finally { _gate.Release(); }
    }
    internal static bool IsClusterAddress(string address)
    {
        if (!System.Net.IPAddress.TryParse(address, out var ip)) return false;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (System.Net.IPAddress.IsLoopback(ip)) return true;
        byte[] bytes = ip.GetAddressBytes();
        return bytes.Length == 4 ? bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31
            || bytes[0] == 192 && bytes[1] == 168 : (bytes[0] & 0xfe) == 0xfc;
    }
    internal bool Authenticate(string id, string? authorization)
    {
        var host = Get(id);
        var expected = host is null ? null : credentials.Resolve(host.CredentialReference);
        if (expected is null || authorization is null || !authorization.StartsWith("Bearer ", StringComparison.Ordinal)) return false;
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(expected)),
            SHA256.HashData(Encoding.UTF8.GetBytes(authorization[7..])));
    }
}
