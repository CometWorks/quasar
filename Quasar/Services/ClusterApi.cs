using Quasar.Models;
using Quasar.Services.Auth;
using System.Text.Json;
using System.Text.Json.Serialization;
using Admin = CometWorks.ClusterGateway.AdminContract.V1;

namespace Quasar.Services;

internal static class ClusterApi
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static void MapClusterApi(this WebApplication app, QuasarAuthOptions authOptions)
    {
        RouteGroupBuilder routes = app.MapGroup("/api/v1/clusters");
        routes.MapGet("", (HttpContext context, ClusterCatalog catalog) =>
        {
            SetProtocolHeader(context);
            ClusterSummary[] clusters = catalog.GetClusters().Select(cluster => new ClusterSummary(
                cluster.UniqueName, cluster.DisplayName, cluster.GatewayUrl,
                cluster.ConfigProfileId, cluster.WorldTemplateId)).ToArray();
            return Results.Json(Envelope(clusters), JsonOptions);
        });
        routes.MapGet("/{uniqueName}/health", (string uniqueName, HttpContext context, ClusterCatalog catalog,
            ClusterGatewayClient client, CancellationToken cancellationToken) =>
            Query(uniqueName, context, catalog, client.GetHealthAsync, cancellationToken));
        routes.MapGet("/{uniqueName}/status", (string uniqueName, HttpContext context, ClusterCatalog catalog,
            ClusterGatewayClient client, CancellationToken cancellationToken) =>
            Query(uniqueName, context, catalog, client.GetStatusAsync, cancellationToken));
        routes.MapGet("/{uniqueName}/plan", (string uniqueName, HttpContext context, ClusterCatalog catalog,
            ClusterGatewayClient client, CancellationToken cancellationToken) =>
            Query(uniqueName, context, catalog, client.GetPlanAsync, cancellationToken));
        routes.MapGet("/{uniqueName}/recovery-readiness", (string uniqueName, HttpContext context,
            ClusterCatalog catalog, ClusterGatewayClient client, CancellationToken cancellationToken) =>
            Query(uniqueName, context, catalog, client.GetRecoveryReadinessAsync, cancellationToken));
        if (authOptions.Enabled)
            routes.RequireAuthorization(QuasarPolicyNames.CanView);
    }

    private static async Task<IResult> Query<T>(string uniqueName, HttpContext context, ClusterCatalog catalog,
        Func<ClusterDefinition, CancellationToken, Task<Admin.AdminEnvelope<T>>> query,
        CancellationToken cancellationToken)
    {
        SetProtocolHeader(context);
        ClusterDefinition? cluster = catalog.GetCluster(uniqueName);
        if (cluster == null)
            return Error(StatusCodes.Status404NotFound, "unknown_cluster", $"Unknown cluster '{uniqueName}'.");
        try
        {
            return Results.Json(await query(cluster, cancellationToken), JsonOptions);
        }
        catch (ClusterGatewayException exception)
        {
            return Error((int)exception.StatusCode, exception.Code, exception.Message);
        }
    }

    private static Admin.AdminEnvelope<T> Envelope<T>(T data) =>
        new(Admin.AdminProtocol.Version, DateTimeOffset.UtcNow, data);

    private static IResult Error(int status, string code, string message) => Results.Json(
        new Admin.AdminErrorEnvelope(Admin.AdminProtocol.Version, DateTimeOffset.UtcNow,
            new Admin.AdminError(code, message)), JsonOptions, statusCode: status);

    private static void SetProtocolHeader(HttpContext context) =>
        context.Response.Headers["X-Cluster-Gateway-Protocol"] = Admin.AdminProtocol.Version.ToString();

    private sealed record ClusterSummary(string UniqueName, string DisplayName, string GatewayUrl,
        string ConfigProfileId, string WorldTemplateId);
}
