using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Quasar.Services.Auth;

public sealed class TrustedNetworkEvaluator
{
    private readonly QuasarAuthOptions _options;

    public TrustedNetworkEvaluator(QuasarAuthOptions options)
    {
        _options = options;
    }

    public bool IsTrusted(HttpContext context)
    {
        var remoteIp = context.Connection.RemoteIpAddress;
        if (remoteIp is null)
            return false;

        if (remoteIp.IsIPv4MappedToIPv6)
            remoteIp = remoteIp.MapToIPv4();

        if (_options.TrustedNetworkBypass.AllowLoopback && IPAddress.IsLoopback(remoteIp))
            return true;

        return _options.TrustedNetworkBypass.AllowSameSubnet && IsOnLocalSubnet(remoteIp);
    }

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
