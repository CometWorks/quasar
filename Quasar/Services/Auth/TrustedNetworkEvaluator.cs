using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Quasar.Services.Auth;

public sealed class TrustedNetworkEvaluator
{
    private static readonly string[] ForwardingHeaders =
    [
        "Forwarded",
        "X-Forwarded-For",
        "X-Forwarded-Host",
        "X-Forwarded-Proto",
        "X-Real-IP",
    ];

    private readonly QuasarAuthOptions _options;

    public TrustedNetworkEvaluator(QuasarAuthOptions options)
    {
        _options = options;
    }

    public bool IsTrusted(HttpContext context)
    {
        if (HasForwardingHeader(context.Request) && !HasHeader(context.Request, "X-Original-For"))
            return false;

        var remoteIp = context.Connection.RemoteIpAddress;
        if (remoteIp is null)
            return false;

        if (remoteIp.IsIPv4MappedToIPv6)
            remoteIp = remoteIp.MapToIPv4();

        if (_options.TrustedNetworkBypass.AllowLoopback && IPAddress.IsLoopback(remoteIp))
            return true;

        return _options.TrustedNetworkBypass.AllowSameSubnet && IsOnLocalSubnet(remoteIp);
    }

    private static bool HasForwardingHeader(HttpRequest request) =>
        ForwardingHeaders.Any(headerName => HasHeader(request, headerName));

    private static bool HasHeader(HttpRequest request, string headerName) =>
        request.Headers.TryGetValue(headerName, out var values) &&
        values.Any(value => !string.IsNullOrWhiteSpace(value));

    private static bool IsOnLocalSubnet(IPAddress remoteIp)
    {
        if (remoteIp.AddressFamily != AddressFamily.InterNetwork)
            return false;

        foreach (var address in GetLocalIPv4UnicastAddresses())
        {
            if (address.IPv4Mask is null)
                continue;

            if (IsSameSubnet(remoteIp, address.Address, address.IPv4Mask))
                return true;
        }

        return false;
    }

    private static IEnumerable<UnicastIPAddressInformation> GetLocalIPv4UnicastAddresses()
    {
        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.OperationalStatus != OperationalStatus.Up)
                continue;

            if (networkInterface.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;

            foreach (var address in networkInterface.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily == AddressFamily.InterNetwork)
                    yield return address;
            }
        }
    }

    private static bool IsSameSubnet(IPAddress remoteIp, IPAddress localIp, IPAddress mask)
    {
        var remoteBytes = remoteIp.GetAddressBytes();
        var localBytes = localIp.GetAddressBytes();
        var maskBytes = mask.GetAddressBytes();

        if (remoteBytes.Length != localBytes.Length || localBytes.Length != maskBytes.Length)
            return false;

        for (var index = 0; index < maskBytes.Length; index++)
        {
            if ((remoteBytes[index] & maskBytes[index]) != (localBytes[index] & maskBytes[index]))
                return false;
        }

        return true;
    }
}
