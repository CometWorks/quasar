using Quasar.Services.Auth;

namespace Quasar.Services;

internal static class ClusterHostEnrollmentApi
{
    internal static void MapClusterHostEnrollmentApi(this WebApplication app)
    {
        var management = app.MapGroup("/api/v1/host-enrollment").RequireAuthorization(QuasarPolicyNames.CanManageSecurity);
        management.AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context); }
            catch (ArgumentException error) { return Results.Problem(error.Message, statusCode: 400); }
            catch (KeyNotFoundException error) { return Results.Problem(error.Message, statusCode: 404); }
            catch (Exception error) when (error is InvalidOperationException or IOException or PlatformNotSupportedException or System.ComponentModel.Win32Exception)
            { return Results.Problem(error.Message, statusCode: 409); }
        });
        management.MapGet("", (ClusterHostCatalog hosts, ClusterHostTunnels tunnels) =>
            hosts.GetAll().Select(h => new { h.Id, h.Name, h.Address, connected = tunnels.IsConnected(h.Id) }));
        management.MapPost("", async (HostEnrollmentRequest request, ClusterHostCatalog hosts, CancellationToken token) =>
            await hosts.RegisterAsync(request.Id, request.Name, request.Address, request.CommandPort, token));
        management.MapPost("/{host}/ticket", (string host, HostEnrollmentOrigin request, HttpContext context, ClusterHostInstaller installer) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return installer.Issue(host, request.QuasarUrl);
        });
        management.MapPost("/{host}/local", async (string host, HostEnrollmentOrigin request, ClusterHostInstaller installer, CancellationToken token) =>
        {
            await installer.InstallLocalAsync(host, request.QuasarUrl, token);
            return Results.Ok();
        });
        management.MapPost("/{host}/ssh", async (string host, HostEnrollmentSsh request, ClusterHostInstaller installer, CancellationToken token) =>
        {
            await installer.InstallSshAsync(host, request.QuasarUrl, request.Ssh, token);
            return Results.Ok();
        });
        app.MapGet("/api/v1/hosts/{host}/connect", async (string host, HttpContext context, ClusterHostCatalog hosts, ClusterHostTunnels tunnels) =>
        {
            if (!hosts.Authenticate(host, context.Request.Headers.Authorization)) { context.Response.StatusCode = 401; return; }
            if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
            await tunnels.ConnectAsync(host, context);
        }).AllowAnonymous();
        app.MapGet("/api/v1/hosts/{host}/tunnels/{id:guid}", async (string host, Guid id, HttpContext context, ClusterHostCatalog hosts, ClusterHostTunnels tunnels) =>
        {
            if (!hosts.Authenticate(host, context.Request.Headers.Authorization)) { context.Response.StatusCode = 401; return; }
            if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
            await tunnels.AcceptTunnelAsync(host, id, context);
        }).AllowAnonymous();
        app.MapGet("/api/v1/host-enrollment/{id:guid}", (Guid id, HttpContext context, ClusterHostInstaller installer) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var script = installer.Redeem(id, context.Request.Headers.Authorization);
            return script is null ? Results.Unauthorized() : Results.Bytes(script, "text/x-shellscript");
        }).AllowAnonymous();
    }
}

public sealed record HostEnrollmentRequest(string Id, string Name, string Address, int CommandPort = 18400);
public sealed record HostEnrollmentOrigin(string QuasarUrl);
public sealed record HostEnrollmentSsh(string QuasarUrl, HostSshInstall Ssh);
