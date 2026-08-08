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
        app.MapGet("/health", (HttpContext context) =>
        {
            SetProtocolHeader(context);
            return Results.Json(Envelope(new QuasarServiceHealth("quasar", true)), JsonOptions);
        });
        app.MapGet("/ready", (HttpContext context, ClusterOperationStore operations) =>
        {
            SetProtocolHeader(context);
            return Results.Json(Envelope(new QuasarServiceReadiness(operations.IsReady)), JsonOptions,
                statusCode: operations.IsReady ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
        });
        RouteGroupBuilder routes = app.MapGroup("/api/v1/clusters");
        routes.MapGet("", (HttpContext context, ClusterCatalog catalog) =>
        {
            SetProtocolHeader(context);
            ClusterSummary[] clusters = catalog.GetClusters()
                .Where(cluster => context.User.CanQueryCluster(cluster.UniqueName))
                .Select(cluster => new ClusterSummary(
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
        routes.MapGet("/{uniqueName}/config", (string uniqueName, HttpContext context,
            ClusterCatalog catalog, ClusterGatewayClient client, CancellationToken cancellationToken) =>
            Query(uniqueName, context, catalog, client.GetPolicyAsync, cancellationToken));
        RouteHandlerBuilder setConfig = routes.MapPut("/{uniqueName}/config", SetPolicy);
        routes.MapGet("/{uniqueName}/operations/{operationId}", GetOperation);
        if (authOptions.Enabled)
        {
            routes.RequireAuthorization(QuasarPolicyNames.ClusterQuery);
            setConfig.RequireAuthorization(QuasarPolicyNames.ClusterManage);
        }
    }

    private static async Task<IResult> Query<T>(string uniqueName, HttpContext context, ClusterCatalog catalog,
        Func<ClusterDefinition, CancellationToken, Task<Admin.AdminEnvelope<T>>> query,
        CancellationToken cancellationToken)
    {
        SetProtocolHeader(context);
        ClusterDefinition? cluster = catalog.GetCluster(uniqueName);
        if (cluster == null)
            return Error(StatusCodes.Status404NotFound, "unknown_cluster", $"Unknown cluster '{uniqueName}'.");
        if (!context.User.CanQueryCluster(uniqueName))
            return Error(StatusCodes.Status403Forbidden, "cluster_forbidden",
                "The credential cannot access this cluster.");
        try
        {
            return Results.Json(await query(cluster, cancellationToken), JsonOptions);
        }
        catch (ClusterGatewayException exception)
        {
            return Error((int)exception.StatusCode, exception.Code, exception.Message);
        }
    }

    private static async Task<IResult> SetPolicy(string uniqueName, Admin.ClusterPolicy policy,
        HttpContext context, ClusterCatalog catalog, ClusterGatewayClient client,
        ClusterOperationStore operations, CancellationToken cancellationToken)
    {
        SetProtocolHeader(context);
        ClusterDefinition? cluster = catalog.GetCluster(uniqueName);
        if (cluster == null)
            return Error(StatusCodes.Status404NotFound, "unknown_cluster", $"Unknown cluster '{uniqueName}'.");
        if (!context.User.CanQueryCluster(uniqueName))
            return Error(StatusCodes.Status403Forbidden, "cluster_forbidden",
                "The credential cannot access this cluster.");
        try
        {
            ClusterOperation operation = await operations.ExecuteAsync(uniqueName, "cluster.config.set",
                context.Request.Headers["Idempotency-Key"].ToString(),
                context.User.Identity?.Name ?? "anonymous", policy,
                token => client.SetPolicyAsync(cluster, policy, token), cancellationToken);
            context.Response.Headers.Location = $"/api/v1/clusters/{Uri.EscapeDataString(uniqueName)}"
                + $"/operations/{operation.OperationId}";
            return Results.Json(Envelope(operation), JsonOptions, statusCode: StatusCodes.Status202Accepted);
        }
        catch (ClusterOperationConflictException exception)
        {
            return Error(exception.StatusCode, exception.Code, exception.Message);
        }
        catch (ClusterOperationStoreUnavailableException exception)
        {
            return Error(StatusCodes.Status503ServiceUnavailable, "operation_store_unavailable", exception.Message);
        }
    }

    private static IResult GetOperation(string uniqueName, string operationId, HttpContext context,
        ClusterCatalog catalog, ClusterOperationStore operations)
    {
        SetProtocolHeader(context);
        if (catalog.GetCluster(uniqueName) == null)
            return Error(StatusCodes.Status404NotFound, "unknown_cluster", $"Unknown cluster '{uniqueName}'.");
        if (!context.User.CanQueryCluster(uniqueName))
            return Error(StatusCodes.Status403Forbidden, "cluster_forbidden",
                "The credential cannot access this cluster.");
        ClusterOperation? operation = operations.Get(operationId);
        return operation == null || !operation.Cluster.Equals(uniqueName, StringComparison.OrdinalIgnoreCase)
            ? Error(StatusCodes.Status404NotFound, "unknown_operation", $"Unknown operation '{operationId}'.")
            : Results.Json(Envelope(operation), JsonOptions);
    }

    private static Admin.AdminEnvelope<T> Envelope<T>(T data) =>
        new(Admin.AdminProtocol.Version, DateTimeOffset.UtcNow, data);

    private static IResult Error(int status, string code, string message) => Results.Json(
        new Admin.AdminErrorEnvelope(Admin.AdminProtocol.Version, DateTimeOffset.UtcNow,
            new Admin.AdminError(code, message)), JsonOptions, statusCode: status);

    private static void SetProtocolHeader(HttpContext context) =>
        context.Response.Headers["X-Cluster-Gateway-Protocol"] = Admin.AdminProtocol.Version.ToString();

    internal static Task WriteAuthorizationErrorAsync(
        HttpContext context, int status, string code, string message)
    {
        SetProtocolHeader(context);
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new Admin.AdminErrorEnvelope(
            Admin.AdminProtocol.Version,
            DateTimeOffset.UtcNow,
            new Admin.AdminError(code, message)), JsonOptions);
    }

    private sealed record ClusterSummary(string UniqueName, string DisplayName, string GatewayUrl,
        string ConfigProfileId, string WorldTemplateId);

    private sealed record QuasarServiceHealth(string Service, bool Live);
    private sealed record QuasarServiceReadiness(bool Ready);
}
