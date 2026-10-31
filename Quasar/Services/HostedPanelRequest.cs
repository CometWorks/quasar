using System.Net;
using Microsoft.AspNetCore.Authentication.Cookies;
using Quasar.Hosting;

namespace Quasar.Services;

internal static class HostedPanelRequest
{
    public const string Header = "X-Quasar-Hosted";
    private static readonly object Key = new();
    public static bool IsHosted(HttpContext context) => context.Items[Key] is true;

    public static async Task InvokeAsync(HttpContext context, RequestDelegate next, Auth.QuasarAuthOptions auth)
    {
        if (!context.Request.Headers.TryGetValue(Header, out var marker)) { await next(context); return; }
        var peer = context.Connection.RemoteIpAddress;
        if (peer?.IsIPv4MappedToIPv6 == true) peer = peer.MapToIPv4();
        var state = HostedPanelState.Read();
        if (peer is null || !IPAddress.IsLoopback(peer) || marker.Count != 1
            || state is not { Choice: "hosted", Credential: not null } || !auth.Enabled || !auth.Steam.Enabled
            || !HostedPanelState.SecretEquals(marker[0], state.LocalProxySecret)
            || !context.Request.Path.StartsWithSegments("/" + state.PanelId, out var remaining))
        { context.Response.StatusCode = 403; return; }
        context.Items[Key] = true;
        context.Request.PathBase = "/" + state.PanelId;
        context.Request.Path = remaining.HasValue ? remaining : "/";
        context.Request.Headers.Remove(Header);
        context.Response.OnStarting(() =>
        {
            var location = context.Response.Headers.Location.ToString();
            var prefix = context.Request.PathBase.Value!;
            if (location.StartsWith('/') && !location.StartsWith("//", StringComparison.Ordinal)
                && !new PathString(location.Split('?')[0]).StartsWithSegments(prefix))
                context.Response.Headers.Location = prefix + location;
            return Task.CompletedTask;
        });
        // The privately authenticated connector owns the prefix; untrusted forwarded
        // prefixes alone never opt a request into hosted mode or trusted-network auth.
        context.Response.Headers["Service-Worker-Allowed"] = "/" + state.PanelId + "/";
        await next(context);
    }
}

internal sealed class HostedPanelCookieManager : ICookieManager
{
    private readonly ChunkingCookieManager inner = new();
    private static string Name(HttpContext context, string key) => HostedPanelRequest.IsHosted(context)
        ? key + "." + context.Request.PathBase.Value!.Trim('/') : key;
    private static CookieOptions Options(HttpContext context, CookieOptions options)
    {
        if (HostedPanelRequest.IsHosted(context)) { options.Path = context.Request.PathBase + "/"; options.Secure = true; }
        return options;
    }
    public string? GetRequestCookie(HttpContext context, string key) => inner.GetRequestCookie(context, Name(context, key));
    public void AppendResponseCookie(HttpContext context, string key, string? value, CookieOptions options) =>
        inner.AppendResponseCookie(context, Name(context, key), value, Options(context, options));
    public void DeleteCookie(HttpContext context, string key, CookieOptions options) =>
        inner.DeleteCookie(context, Name(context, key), Options(context, options));
}
