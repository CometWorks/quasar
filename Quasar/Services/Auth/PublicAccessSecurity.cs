using System.Net;

namespace Quasar.Services.Auth;

internal static class PublicAccessSecurity
{
    internal const string HttpsRequiredMessage = "HTTPS is required for non-LAN Quasar access.";

    internal static void ValidateStartup(
        QuasarAuthOptions auth,
        WebServiceOptions web,
        string? aspNetCoreUrls)
    {
        if (auth.Enabled)
            return;

        bool loopbackOnly = string.IsNullOrWhiteSpace(aspNetCoreUrls)
            ? IsLoopbackHost(web.Host)
            : aspNetCoreUrls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                is { Length: > 0 } urls && urls.All(IsLoopbackUrl);

        if (!loopbackOnly)
        {
            throw new InvalidOperationException(
                "Quasar authentication can be disabled only on loopback listeners. " +
                "Set Quasar:Host, QUASAR_WEB_HOST, and every ASPNETCORE_URLS binding to localhost, 127.0.0.1, or ::1.");
        }
    }

    // Runs after ForwardedHeadersMiddleware so only a trusted proxy can establish HTTPS.
    internal static Task InvokeAsync(HttpContext context, RequestDelegate next, QuasarAuthOptions auth)
    {
        if (!auth.RequireHttpsForPublicAccess || context.Request.IsHttps ||
            MachineEndpointSecurity.IsMachinePath(context.Request.Path) ||
            TrustedNetworkEvaluator.IsTrustedAddress(
                context.Connection.RemoteIpAddress,
                auth.TrustedNetworkBypass))
        {
            return next(context);
        }

        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        context.Response.ContentType = "text/plain; charset=utf-8";
        return context.Response.WriteAsync(HttpsRequiredMessage);
    }

    private static bool IsLoopbackUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
             !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            uri.AbsolutePath != "/")
        {
            return false;
        }

        return IsLoopbackHost(uri.Host);
    }

    private static bool IsLoopbackHost(string host)
    {
        host = host.Trim().Trim('[', ']');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return true;

        return IPAddress.TryParse(host, out var address) && IsLoopbackAddress(address);
    }

    private static bool IsLoopbackAddress(IPAddress? address)
    {
        if (address is null)
            return false;
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        return IPAddress.IsLoopback(address);
    }
}
