using Quasar.Services.Auth;

namespace Quasar.Services;

internal static class WebServiceProbeEndpoints
{
    internal static void MapWebServiceProbes(this IEndpointRouteBuilder endpoints, QuasarAuthOptions authOptions)
    {
        endpoints.MapGet("/api/health", () => Results.Json(new { status = "ok" }));

        endpoints.MapGet("/api/ready", (ClusterOperationStore operations) =>
            Results.Json(new { status = operations.IsReady ? "ready" : "not-ready" },
                statusCode: operations.IsReady
                    ? StatusCodes.Status200OK
                    : StatusCodes.Status503ServiceUnavailable));

        endpoints.MapGet("/api/discovery", (WebServiceState state) =>
            Results.Json(state.CurrentManifest));

        var version = endpoints.MapGet("/api/version", (HttpContext context, WebServiceOptions options) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Json(new { version = options.Version });
        });
        if (authOptions.Enabled)
            version.RequireAuthorization(QuasarPolicyNames.CanView);
    }
}
