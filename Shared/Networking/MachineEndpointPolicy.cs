using System.Net;
using System.Net.Sockets;

namespace Quasar.Networking;

/// <summary>Additional transport gates for credential-authenticated machine endpoints.</summary>
public sealed class MachineEndpointPolicy
{
    public static readonly string[] LoopbackSources = ["127.0.0.1", "::1"];
    private readonly IPNetwork[] _sources;
    private readonly HashSet<string> _hosts;

    public MachineEndpointPolicy(IEnumerable<string> sources, IEnumerable<string> hosts)
    {
        _sources = sources.Select(ParseSource).ToArray();
        _hosts = hosts.Select(ValidateAuthority).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (_sources.Length == 0 || _hosts.Count == 0)
            throw new ArgumentException("Machine endpoints require explicit sources and destination authorities.");
    }

    public bool Allows(IPAddress? socketPeer, bool hasOrigin, string authority)
    {
        if (hasOrigin || socketPeer is null || !_hosts.Contains(authority)) return false;
        if (socketPeer.IsIPv4MappedToIPv6) socketPeer = socketPeer.MapToIPv4();
        return _sources.Any(network => network.Contains(socketPeer));
    }

    private static IPNetwork ParseSource(string value)
    {
        if (IPAddress.TryParse(value, out var address))
        {
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            value = address + (address.AddressFamily == AddressFamily.InterNetwork ? "/32" : "/128");
        }
        if (!IPNetwork.TryParse(value, out var network))
            throw new ArgumentException("Machine source must be an explicit private IP or CIDR.");
        var ip = network.BaseAddress;
        byte[] bytes = ip.GetAddressBytes();
        bool privateSource = ip.AddressFamily == AddressFamily.InterNetwork
            ? network.PrefixLength >= 16 && (bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31
                || bytes[0] == 192 && bytes[1] == 168 || bytes[0] == 127)
            : network.PrefixLength >= 48 && ((bytes[0] & 0xfe) == 0xfc
                || ip.Equals(IPAddress.IPv6Loopback) && network.PrefixLength == 128);
        if (!privateSource)
            throw new ArgumentException("Machine sources must be private or loopback; minimum CIDR prefix is /16 for IPv4 or /48 for IPv6. Public and wildcard sources are forbidden.");
        return network;
    }

    private static string ValidateAuthority(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(c => char.IsWhiteSpace(c) || c is '/' or '\\' or '@' or '?' or '#' or '*' or '+' or ',')
            || !Uri.TryCreate("http://" + value, UriKind.Absolute, out var uri)
            || uri.HostNameType == UriHostNameType.Unknown || uri.Port == 0 || uri.Host.EndsWith('.')
            || uri.Host is "0.0.0.0" or "[::]" or "::")
            throw new ArgumentException("Machine destination must be an exact DNS/IP authority, optionally including a port; wildcards are forbidden.");
        return value;
    }
}
