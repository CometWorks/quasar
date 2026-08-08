using Quasar.Models;
using Quasar.Services.Auth;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.Text.Json.Serialization;
using Admin = CometWorks.ClusterGateway.AdminContract.V1;
using HostContract = global::Quasar.Host.Contract.V1;

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
                    cluster.ConfigProfileId, cluster.WorldTemplateId, cluster.GoalState)).ToArray();
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
        routes.MapGet("/{uniqueName}/host", GetHostStatus);
        routes.MapGet("/{uniqueName}/lifecycle", GetLifecycleStatus);
        RouteHandlerBuilder setConfig = routes.MapPut("/{uniqueName}/config", SetPolicy);
        RouteHandlerBuilder setGoal = routes.MapPut("/{uniqueName}/goal", SetGoal);
        RouteHandlerBuilder setGatewaySpec = routes.MapPut("/{uniqueName}/gateway-spec", SetGatewaySpec);
        RouteHandlerBuilder restartGateway = routes.MapPost("/{uniqueName}/gateway/restart", RestartGateway);
        RouteHandlerBuilder applyAttachment = routes.MapPut(
            "/{uniqueName}/host/attachment", ApplyHostAttachment);
        RouteHandlerBuilder applyGateway = routes.MapPut(
            "/{uniqueName}/host/gateway", ApplyHostGateway);
        routes.MapGet("/{uniqueName}/operations/{operationId}", GetOperation);
        if (authOptions.Enabled)
        {
            routes.RequireAuthorization(QuasarPolicyNames.ClusterQuery);
            setConfig.RequireAuthorization(QuasarPolicyNames.ClusterManage);
            setGoal.RequireAuthorization(QuasarPolicyNames.ClusterManage);
            setGatewaySpec.RequireAuthorization(QuasarPolicyNames.ClusterManage);
            restartGateway.RequireAuthorization(QuasarPolicyNames.ClusterManage);
            applyAttachment.RequireAuthorization(QuasarPolicyNames.ClusterManage);
            applyGateway.RequireAuthorization(QuasarPolicyNames.ClusterManage);
        }
    }

    private static IResult GetLifecycleStatus(string uniqueName, HttpContext context,
        ClusterCatalog catalog, ClusterReconciler reconciler)
    {
        SetProtocolHeader(context);
        if (catalog.GetCluster(uniqueName) == null)
            return Error(StatusCodes.Status404NotFound, "unknown_cluster", $"Unknown cluster '{uniqueName}'.");
        if (!context.User.CanQueryCluster(uniqueName))
            return Error(StatusCodes.Status403Forbidden, "cluster_forbidden",
                "The credential cannot access this cluster.");
        return Results.Json(Envelope(reconciler.GetStatus(uniqueName)), JsonOptions);
    }

    private static async Task<IResult> SetGoal(string uniqueName, [FromBody] ClusterGoalRequest request,
        HttpContext context, [FromServices] ClusterCatalog catalog,
        [FromServices] ClusterCommandService commands, CancellationToken cancellationToken)
    {
        SetProtocolHeader(context);
        if (catalog.GetCluster(uniqueName) == null)
            return Error(StatusCodes.Status404NotFound, "unknown_cluster", $"Unknown cluster '{uniqueName}'.");
        if (!context.User.CanQueryCluster(uniqueName))
            return Error(StatusCodes.Status403Forbidden, "cluster_forbidden",
                "The credential cannot access this cluster.");
        try
        {
            ClusterOperation operation = await commands.SetGoalAsync(uniqueName, request,
                context.Request.Headers["Idempotency-Key"].ToString(),
                context.User.Identity?.Name ?? "anonymous", cancellationToken);
            return AcceptedOperation(uniqueName, context, operation);
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

    private static async Task<IResult> SetGatewaySpec(string uniqueName,
        [FromBody] HostContract.GatewaySpec gateway, HttpContext context,
        [FromServices] ClusterCatalog catalog, [FromServices] ClusterOperationStore operations,
        CancellationToken cancellationToken)
    {
        SetProtocolHeader(context);
        if (catalog.GetCluster(uniqueName) == null)
            return Error(StatusCodes.Status404NotFound, "unknown_cluster", $"Unknown cluster '{uniqueName}'.");
        if (!context.User.CanQueryCluster(uniqueName))
            return Error(StatusCodes.Status403Forbidden, "cluster_forbidden",
                "The credential cannot access this cluster.");
        try
        {
            gateway = ClusterCatalog.NormalizeGatewaySpec(uniqueName, gateway);
        }
        catch (ArgumentException exception)
        {
            return Error(StatusCodes.Status400BadRequest, "invalid_gateway_spec", exception.Message);
        }
        try
        {
            ClusterOperation operation = await operations.ExecuteAsync(uniqueName, "cluster.gateway-spec.set",
                context.Request.Headers["Idempotency-Key"].ToString(),
                context.User.Identity?.Name ?? "anonymous", gateway, async token =>
                {
                    ClusterDefinition updated = await catalog.SetGatewayAsync(uniqueName, gateway, token);
                    return Envelope(updated.Gateway!);
                }, cancellationToken);
            return AcceptedOperation(uniqueName, context, operation);
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

    private static async Task<IResult> RestartGateway(string uniqueName,
        [FromBody] Admin.GatewayRestartRequest request, HttpContext context,
        [FromServices] ClusterCatalog catalog, [FromServices] ClusterGatewayClient client,
        [FromServices] ClusterOperationStore operations, CancellationToken cancellationToken)
    {
        SetProtocolHeader(context);
        ClusterDefinition? cluster = catalog.GetCluster(uniqueName);
        if (cluster == null)
            return Error(StatusCodes.Status404NotFound, "unknown_cluster", $"Unknown cluster '{uniqueName}'.");
        if (!context.User.CanQueryCluster(uniqueName))
            return Error(StatusCodes.Status403Forbidden, "cluster_forbidden",
                "The credential cannot access this cluster.");
        if (cluster.GoalState != DedicatedServerGoalState.On)
            return Error(StatusCodes.Status409Conflict, "cluster_not_on",
                "Gateway restart requires cluster goal On.");
        if (request.RequestId == Guid.Empty)
            return Error(StatusCodes.Status400BadRequest, "request_id_required",
                "A Gateway restart request ID is required.");
        try
        {
            ClusterOperation operation = await operations.ExecuteAsync(uniqueName, "cluster.gateway.restart",
                context.Request.Headers["Idempotency-Key"].ToString(),
                context.User.Identity?.Name ?? "anonymous", request,
                token => client.RestartGatewayAsync(cluster, request, token), cancellationToken);
            return AcceptedOperation(uniqueName, context, operation);
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

    private static async Task<IResult> GetHostStatus(string uniqueName, HttpContext context,
        ClusterCatalog catalog, ClusterHostClient client, CancellationToken cancellationToken)
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
            HostContract.HostEnvelope<HostContract.HostStatus> result =
                await client.GetStatusAsync(cluster, cancellationToken);
            return Results.Json(new Admin.AdminEnvelope<HostContract.HostStatus>(
                Admin.AdminProtocol.Version, result.CapturedAt, result.Data), JsonOptions);
        }
        catch (ClusterHostException exception)
        {
            return Error((int)exception.StatusCode, exception.Code, exception.Message);
        }
    }

    private static async Task<IResult> ApplyHostAttachment(string uniqueName,
        HostContract.HostAttachmentSpec attachment, HttpContext context, ClusterCatalog catalog,
        ClusterHostClient client, ClusterOperationStore operations, CancellationToken cancellationToken)
    {
        SetProtocolHeader(context);
        ClusterDefinition? cluster = catalog.GetCluster(uniqueName);
        if (cluster == null)
            return Error(StatusCodes.Status404NotFound, "unknown_cluster", $"Unknown cluster '{uniqueName}'.");
        if (!context.User.CanQueryCluster(uniqueName))
            return Error(StatusCodes.Status403Forbidden, "cluster_forbidden",
                "The credential cannot access this cluster.");
        if (!string.Equals(attachment.ClusterId, uniqueName, StringComparison.OrdinalIgnoreCase))
            return Error(StatusCodes.Status400BadRequest, "cluster_id_mismatch",
                "Host attachment cluster ID must match the route cluster.");
        try
        {
            ClusterOperation operation = await operations.ExecuteAsync(uniqueName,
                "cluster.host.attachment.apply", context.Request.Headers["Idempotency-Key"].ToString(),
                context.User.Identity?.Name ?? "anonymous", attachment, async token =>
                {
                    HostContract.HostEnvelope<HostContract.HostAttachmentStatus> result =
                        await client.ApplyAttachmentAsync(cluster, attachment, token);
                    return new Admin.AdminEnvelope<HostContract.HostAttachmentStatus>(
                        Admin.AdminProtocol.Version, result.CapturedAt, result.Data);
                }, cancellationToken);
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

    private static async Task<IResult> ApplyHostGateway(string uniqueName,
        [FromBody] HostContract.GatewaySpec gateway, HttpContext context,
        [FromServices] ClusterCatalog catalog,
        [FromServices] ClusterHostClient client,
        [FromServices] ClusterGatewayClient gatewayClient,
        [FromServices] ClusterOperationStore operations,
        CancellationToken cancellationToken)
    {
        SetProtocolHeader(context);
        ClusterDefinition? cluster = catalog.GetCluster(uniqueName);
        if (cluster == null)
            return Error(StatusCodes.Status404NotFound, "unknown_cluster", $"Unknown cluster '{uniqueName}'.");
        if (!context.User.CanQueryCluster(uniqueName))
            return Error(StatusCodes.Status403Forbidden, "cluster_forbidden",
                "The credential cannot access this cluster.");
        if (!string.Equals(gateway.ClusterId, uniqueName, StringComparison.OrdinalIgnoreCase))
            return Error(StatusCodes.Status400BadRequest, "cluster_id_mismatch",
                "Gateway spec cluster ID must match the route cluster.");
        try
        {
            ClusterOperation operation = await operations.ExecuteAsync(uniqueName,
                "cluster.host.gateway.apply", context.Request.Headers["Idempotency-Key"].ToString(),
                context.User.Identity?.Name ?? "anonymous", gateway, async token =>
                {
                    if (gateway.Goal == HostContract.GatewayGoal.Off)
                    {
                        Admin.ClusterStatus status = (await gatewayClient.GetStatusAsync(cluster, token)).Data;
                        EnsureGatewayCanStop(status);
                    }
                    HostContract.HostEnvelope<HostContract.GatewayStatus> result =
                        await client.ApplyGatewayAsync(cluster, gateway, token);
                    return new Admin.AdminEnvelope<HostContract.GatewayStatus>(
                        Admin.AdminProtocol.Version, result.CapturedAt, result.Data);
                }, cancellationToken);
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

    internal static void EnsureGatewayCanStop(Admin.ClusterStatus status)
    {
        if (status.Phase != Admin.ClusterPhase.Down || status.LastCleanShutdown is null)
            throw new ClusterGatewayException(System.Net.HttpStatusCode.Conflict,
                "cluster_not_cleanly_down",
                "Gateway goal Off requires phase Down and a clean-shutdown marker.");
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

    private static IResult AcceptedOperation(string uniqueName, HttpContext context, ClusterOperation operation)
    {
        context.Response.Headers.Location = $"/api/v1/clusters/{Uri.EscapeDataString(uniqueName)}"
            + $"/operations/{operation.OperationId}";
        return Results.Json(Envelope(operation), JsonOptions, statusCode: StatusCodes.Status202Accepted);
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
        string ConfigProfileId, string WorldTemplateId, DedicatedServerGoalState Goal);

    private sealed record QuasarServiceHealth(string Service, bool Live);
    private sealed record QuasarServiceReadiness(bool Ready);
}
