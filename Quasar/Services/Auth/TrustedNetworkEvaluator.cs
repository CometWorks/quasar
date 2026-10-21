using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.AspNetCore.HttpOverrides;

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

    private static readonly object OriginalRequestKey = new();
    private readonly QuasarAuthOptions _options;
    private readonly WebServiceOptions _web;

    public TrustedNetworkEvaluator(QuasarAuthOptions options, WebServiceOptions web)
    {
        _options = options;
        _web = web;
    }

    // Capture server-owned evidence before ForwardedHeadersMiddleware changes the request.
    // Client-supplied X-Original-* headers are never evidence of a trusted proxy.
    public void CaptureOriginalRequest(HttpContext context, ForwardedHeadersOptions forwarding)
    {
        var request = context.Request;
        var peer = context.Connection.RemoteIpAddress;
        var knownProxy = peer is not null && IsKnownProxy(peer, forwarding);
        var hasForwarding = HasForwardingHeader(request);
        var permittedForwarding = !hasForwarding || knownProxy &&
            request.Headers["X-Forwarded-For"].Count == 1 &&
            IPEndPoint.TryParse(request.Headers["X-Forwarded-For"].ToString(), out _) &&
            !request.Headers.ContainsKey("Forwarded") && !request.Headers.ContainsKey("X-Real-IP");
        context.Items[OriginalRequestKey] = permittedForwarding && IsAllowedDestination(context);
    }

    private static bool IsKnownProxy(IPAddress peer, ForwardedHeadersOptions forwarding)
    {
        if (forwarding.KnownProxies.Contains(peer) || forwarding.KnownIPNetworks.Any(network => network.Contains(peer)))
            return true;
        return peer.IsIPv4MappedToIPv6 && IsKnownProxy(peer.MapToIPv4(), forwarding);
    }

    public bool IsTrusted(HttpContext context)
    {
        if (context.Items[OriginalRequestKey] is not true ||
            HasForwardingHeader(context.Request) ||
            !IsAllowedDestination(context) || !HasSameOriginContext(context.Request))
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

    private bool IsAllowedDestination(HttpContext context)
    {
        var request = context.Request;
        if (!TryOrigin($"{request.Scheme}://{request.Host}", out var destination))
            return false;
        if (TryOrigin(_web.BaseUrl, out var configured, allowPath: true) && SameOrigin(destination, configured))
            return true;
        if (TryOrigin(_web.ListenUrl, out var listener) &&
            listener.Host is not ("0.0.0.0" or "::" or "[::]") && SameOrigin(destination, listener))
            return true;

        // Wildcard listeners authorize concrete local destinations, never arbitrary DNS names.
        if (destination.Port != context.Connection.LocalPort)
            return false;
        if (destination.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return true;
        return IPAddress.TryParse(destination.Host.Trim('[', ']'), out var address) &&
            (IPAddress.IsLoopback(address) || SameAddress(address, context.Connection.LocalIpAddress));
    }

    private static bool SameAddress(IPAddress left, IPAddress? right) =>
        (left.IsIPv4MappedToIPv6 ? left.MapToIPv4() : left).Equals(
            right?.IsIPv4MappedToIPv6 == true ? right.MapToIPv4() : right);

    private static bool HasSameOriginContext(HttpRequest request)
    {
        if (!TryOrigin($"{request.Scheme}://{request.Host}", out var destination))
            return false;
        if (request.Headers.TryGetValue("Sec-Fetch-Site", out var site) &&
            (site.Count != 1 || site[0] is not ("same-origin" or "none")))
            return false;
        if (request.Headers.TryGetValue("Origin", out var origin))
            return origin.Count == 1 && TryOrigin(origin.ToString(), out var source) && SameOrigin(destination, source);
        if (request.Headers.TryGetValue("Referer", out var referer))
            return referer.Count == 1 && Uri.TryCreate(referer.ToString(), UriKind.Absolute, out var source) &&
                source.UserInfo.Length == 0 && SameOrigin(destination, source);
        return true; // Direct navigation and non-browser bootstrap clients have no context headers.
    }

    private static bool TryOrigin(string value, out Uri origin, bool allowPath = false)
    {
        origin = null!;
        if (value.Any(char.IsWhiteSpace) || value.Contains('\\'))
            return false;
        var authorityStart = value.IndexOf("://", StringComparison.Ordinal);
        if (authorityStart < 0)
            return false;
        var pathStart = value.IndexOf('/', authorityStart + 3);
        if (!allowPath && pathStart >= 0 && value[pathStart..] != "/")
            return false;
        return Uri.TryCreate(value, UriKind.Absolute, out origin!) &&
            origin.Scheme is "http" or "https" && origin.UserInfo.Length == 0 &&
            (allowPath || origin.AbsolutePath == "/") && origin.Query.Length == 0 && origin.Fragment.Length == 0;
    }

    private static bool SameOrigin(Uri left, Uri right) =>
        left.Scheme == right.Scheme && left.Port == right.Port &&
        left.IdnHost.Equals(right.IdnHost, StringComparison.OrdinalIgnoreCase);

    private static bool HasForwardingHeader(HttpRequest request) =>
        ForwardingHeaders.Any(request.Headers.ContainsKey);

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
