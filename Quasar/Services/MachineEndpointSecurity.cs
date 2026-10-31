using Quasar.Networking;

namespace Quasar.Services;

internal static class MachineEndpointSecurity
{
    // ASP.NET's opt-in startup filter otherwise runs before application middleware.
    internal static void DisableAutomaticForwarding(IConfiguration configuration) =>
        configuration["ForwardedHeaders_Enabled"] = "false";

    internal static MachineEndpointPolicy Create(IConfiguration configuration, WebServiceOptions web)
    {
        var section = configuration.GetSection("Quasar:MachineEndpoints");
        string[] Read(string key, string variable, string[] defaults) =>
            Environment.GetEnvironmentVariable(variable) is { } text ? text.Split(';')
                : section.GetSection(key).Exists() ? section.GetSection(key).Get<string[]>() ?? [] : defaults;
        var origin = new Uri(web.BaseUrl);
        if (origin.Scheme is not ("http" or "https") || origin.UserInfo.Length != 0
            || origin.Query.Length != 0 || origin.Fragment.Length != 0)
            throw new ArgumentException("Quasar public base URL must use HTTP(S) without credentials, query or fragment.");
        return new(Read("AllowedSources", "QUASAR_MACHINE_ALLOWED_SOURCES", MachineEndpointPolicy.LoopbackSources),
            Read("AllowedHosts", "QUASAR_MACHINE_ALLOWED_HOSTS",
                [origin.Authority, $"127.0.0.1:{web.Port}", $"localhost:{web.Port}", $"[::1]:{web.Port}"]));
    }

    internal static bool IsMachinePath(PathString path) =>
        path.StartsWithSegments("/ws/agent", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/api/internal", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/api/discovery", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/api/v1/hosts", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/api/v1/host-enrollment", out var remaining)
            && Guid.TryParse(remaining.Value?.Trim('/'), out _);

    // Runs before forwarded headers: only the actual socket peer and original Host count.
    internal static Task InvokeAsync(HttpContext context, RequestDelegate next, MachineEndpointPolicy policy)
    {
        if (IsMachinePath(context.Request.Path)
            && !policy.Allows(context.Connection.RemoteIpAddress, context.Request.Headers.ContainsKey("Origin"), HostedPanelRequest.IsHosted(context)
                ? $"127.0.0.1:{context.Connection.LocalPort}" : context.Request.Host.Value ?? ""))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        }
        return next(context);
    }
}
