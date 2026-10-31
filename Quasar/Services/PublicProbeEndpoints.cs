namespace Quasar.Services;

internal static class PublicProbeEndpoints
{
    internal static void MapPublicProbes(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/health", () => Results.Json(new { status = "ok" }));

        endpoints.MapGet("/api/ready", (ClusterOperationStore operations) =>
            Results.Json(new { status = operations.IsReady ? "ready" : "not-ready" },
                statusCode: operations.IsReady
                    ? StatusCodes.Status200OK
                    : StatusCodes.Status503ServiceUnavailable));

        endpoints.MapGet("/api/discovery", (WebServiceState state) =>
            Results.Json(state.CurrentManifest));
    }
}
