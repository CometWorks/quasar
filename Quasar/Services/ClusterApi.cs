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
            bool ready = operations.IsReady;
            return Results.Json(Envelope(new QuasarServiceReadiness(ready)), JsonOptions,
                statusCode: ready ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
        });
        RouteGroupBuilder routes = app.MapGroup("/api/v1/clusters");
        // Routes without their own handling still answer with the JSON error envelope, not an HTML 500 page.
        routes.AddEndpointFilter(async (invocation, next) =>
        {
            try { return await next(invocation); }
            catch (ClusterOperationStoreUnavailableException error)
            {
                SetProtocolHeader(invocation.HttpContext);
                return Error(503, "operation_store_unavailable", error.Message);
            }
        });
        var setupStatus = routes.MapGet("/{uniqueName}/setup", (string uniqueName, HttpContext context,
            [FromServices] ClusterSetupService setup) =>
        {
            if (!context.User.CanQueryCluster(uniqueName)) return Error(403, "cluster_forbidden", "The credential cannot access this cluster.");
            try { return Results.Json(Envelope(setup.GetStatus(uniqueName)), JsonOptions); }
            catch (ArgumentException error) { return Error(400, "invalid_cluster", error.Message); }
        });
        var setupCluster = routes.MapPost("/setup", async (ClusterSetupRequest request, HttpContext context,
            [FromServices] ClusterSetupService setup, CancellationToken token) =>
        {
            if (!context.User.CanQueryCluster(request.UniqueName)) return Error(403, "cluster_forbidden", "The credential cannot access this cluster.");
            try { return Results.Json(Envelope(await setup.RunAsync(request, context.Request.Headers["Idempotency-Key"].ToString(), context.User.Identity?.Name ?? "anonymous", token)), JsonOptions); }
            catch (ClusterOperationConflictException error) { return Error(error.StatusCode, error.Code, error.Message); }
            catch (ArgumentException error) { return Error(400, "invalid_setup", error.Message); }
        });
        if (authOptions.Enabled)
        {
            setupStatus.RequireAuthorization(QuasarPolicyNames.ClusterManage, QuasarPolicyNames.CanEditConfigs);
            setupCluster.RequireAuthorization(QuasarPolicyNames.ClusterManage, QuasarPolicyNames.CanEditConfigs);
        }
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
        MapRead("diagnostics", (c, d, t) => c.GetDiagnosticsAsync(d, t));
        MapRead("handover-config", (c, d, t) => c.GetHandoverConfigAsync(d, t));
        MapRead("artifacts", (c, d, t) => c.GetArtifactsAsync(d, t));
        routes.MapGet("/{uniqueName}/artifacts/{id}", (string uniqueName, string id, HttpContext context,
            ClusterCatalog catalog, ClusterGatewayClient client, CancellationToken token) =>
            Query(uniqueName, context, catalog, (c, t) => client.GetArtifactAsync(c, id, t), token));
        MapRead("capabilities", (c, d, t) => c.GetCapabilitiesAsync(d, t));
        MapRead("nodes", (c, d, t) => c.GetNodesAsync(d, t));
        MapRead("world-authority", (c, d, t) => c.GetWorldAuthorityAsync(d, t));
        MapRead("snapshots", (c, d, t) => c.GetSnapshotsAsync(d, t));
        MapRead("partitions", (c, d, t) => c.GetPartitionsAsync(d, t));
        MapRead("clients", (c, d, t) => c.GetClientsAsync(d, t));
        MapRead("admission/bans", (c, d, t) => c.GetBansAsync(d, t));
        MapRead("gateway-operations", (c, d, t) => c.GetOperationsAsync(d, t));
        routes.MapGet("/{uniqueName}/gateway-operations/{id}", (string uniqueName, string id,
            HttpContext context, ClusterCatalog catalog, ClusterGatewayClient client, CancellationToken token) =>
            Query(uniqueName, context, catalog, (c, t) => client.GetOperationAsync(c, id, t), token));
        routes.MapGet("/{uniqueName}/events", (string uniqueName, long? cursor, int? limit,
            HttpContext context, ClusterCatalog catalog, ClusterGatewayClient client, CancellationToken token) =>
            Query(uniqueName, context, catalog, (c, t) => client.GetEventsAsync(c, cursor ?? 0, limit ?? 100, t), token));
        routes.MapGet("/{uniqueName}/chat/history", (string uniqueName, long? cursor, int? limit,
            HttpContext context, ClusterCatalog catalog, ClusterGatewayClient client, CancellationToken token) =>
            Query(uniqueName, context, catalog, (c, t) => client.GetChatAsync(c, cursor ?? 0, limit ?? 100, t), token));

        void MapRead<T>(string path,
            Func<ClusterGatewayClient, ClusterDefinition, CancellationToken, Task<Admin.AdminEnvelope<T>>> read) =>
            routes.MapGet("/{uniqueName}/" + path, (string uniqueName, HttpContext context,
                ClusterCatalog catalog, ClusterGatewayClient client, CancellationToken token) =>
                Query(uniqueName, context, catalog, (c, t) => read(client, c, t), token));

        routes.MapGet("/{uniqueName}/fleet", (string uniqueName, HttpContext context,
            ClusterCatalog catalog, [FromServices] ClusterFleetService fleet, CancellationToken token) =>
            Query(uniqueName, context, catalog, (c, t) => fleet.GetAsync(c, t), token));
        routes.MapGet("/{uniqueName}/host", GetHostStatus);
        routes.MapGet("/{uniqueName}/lifecycle", GetLifecycleStatus);
        routes.MapGet("/{uniqueName}/package-release", GetPackageRelease);
        RouteHandlerBuilder stagePackage = routes.MapPut("/{uniqueName}/package", StagePackage);
        routes.MapGet("/{uniqueName}/package-selection", GetPackageSelection);
        RouteHandlerBuilder selectPackage = routes.MapPut("/{uniqueName}/package-selection", SelectPackage);
        routes.MapGet("/{uniqueName}/dependency-candidate", GetDependencyCandidate);
        routes.MapGet("/{uniqueName}/dependencies", GetDependencies);
        RouteHandlerBuilder deploymentInputs = routes.MapGet("/{uniqueName}/deployment-inputs", GetDeploymentInputs);
        routes.MapGet("/{uniqueName}/backups", (string uniqueName, HttpContext context, [FromServices] ClusterBackupService backups) =>
        {
            SetProtocolHeader(context);
            return context.User.CanQueryCluster(uniqueName) ? Results.Json(Envelope(backups.List(uniqueName)), JsonOptions)
                : Error(403, "cluster_forbidden", "The credential cannot access this cluster.");
        });
        routes.MapGet("/{uniqueName}/update", (string uniqueName, HttpContext context, ClusterCatalog catalog) =>
        {
            SetProtocolHeader(context);
            if (!context.User.CanQueryCluster(uniqueName)) return Error(403, "cluster_forbidden", "The credential cannot access this cluster.");
            var cluster = catalog.GetCluster(uniqueName);
            return cluster is null ? Error(404, "unknown_cluster", "Cluster was not found.")
                : Results.Json(Envelope(cluster.Update), JsonOptions);
        });
        RouteHandlerBuilder beginUpdate = routes.MapPost("/{uniqueName}/update", BeginUpdate);
        RouteHandlerBuilder archiveExport = routes.MapPost("/{uniqueName}/artifacts/{id}/backup", (string uniqueName, string id,
            HttpContext context, [FromServices] ClusterBackupService backups, CancellationToken token) => RunBackup(uniqueName, context,
                () => backups.ArchiveExportAsync(uniqueName, id, context.Request.Headers["Idempotency-Key"].ToString(),
                    context.User.Identity?.Name ?? "anonymous", token)));
        RouteHandlerBuilder captureBackup = routes.MapPost("/{uniqueName}/backups", CaptureBackup);
        RouteHandlerBuilder restoreBackup = routes.MapPost("/{uniqueName}/restore", RestoreBackup);
        var convertToCluster = routes.MapPost("/{uniqueName}/convert/from-server", (string uniqueName, ServerToClusterRequest request,
            HttpContext context, [FromServices] ClusterConversionService conversions, CancellationToken token) => RunBackup(uniqueName, context,
                () => conversions.ToClusterAsync(uniqueName, request, context.Request.Headers["Idempotency-Key"].ToString(),
                    context.User.Identity?.Name ?? "anonymous", token)));
        var convertToServer = routes.MapPost("/{uniqueName}/convert/to-server", (string uniqueName, ClusterToServerRequest request,
            HttpContext context, [FromServices] ClusterConversionService conversions, CancellationToken token) => RunBackup(uniqueName, context,
                () => conversions.ToServerAsync(uniqueName, request, context.Request.Headers["Idempotency-Key"].ToString(),
                    context.User.Identity?.Name ?? "anonymous", token)));
        var conversionReview = routes.MapGet("/{uniqueName}/convert/review/{server}", (string uniqueName, string server,
            HttpContext context, [FromServices] ClusterConversionService conversions) =>
        {
            SetProtocolHeader(context);
            if (!context.User.CanQueryCluster(uniqueName)) return Error(403, "cluster_forbidden", "Cluster access denied.");
            try { return Results.Json(Envelope(conversions.ReviewServer(server)), JsonOptions); }
            catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException)
            { return Error(409, "conversion_unavailable", error.Message); }
        });
        var conversionPlugins = routes.MapGet("/{uniqueName}/convert/plugins", async (string uniqueName,
            HttpContext context, [FromServices] ClusterConversionService conversions, CancellationToken token) =>
        {
            SetProtocolHeader(context);
            if (!context.User.CanQueryCluster(uniqueName)) return Error(403, "cluster_forbidden", "Cluster access denied.");
            try { return Results.Json(Envelope(await conversions.ReviewPluginsAsync(uniqueName, token)), JsonOptions); }
            catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or IOException)
            { return Error(409, "conversion_unavailable", error.Message); }
        });
        var conversionStatus = routes.MapGet("/{uniqueName}/conversions/{id:guid}", (string uniqueName, Guid id,
            HttpContext context, [FromServices] ClusterConversionService conversions) =>
        {
            SetProtocolHeader(context);
            if (!context.User.CanQueryCluster(uniqueName)) return Error(403, "cluster_forbidden", "Cluster access denied.");
            var status = conversions.GetStatus(id);
            return status?.Cluster == uniqueName ? Results.Json(Envelope(status), JsonOptions) : Error(404, "unknown_conversion", "Conversion not found.");
        });
        if (authOptions.Enabled)
            foreach (var route in new[] { convertToCluster, convertToServer, conversionReview, conversionPlugins, conversionStatus })
                route.RequireAuthorization(QuasarPolicyNames.ClusterManage, QuasarPolicyNames.CanEditServers, QuasarPolicyNames.CanEditConfigs);
        RouteHandlerBuilder createCluster = routes.MapPost("/", CreateCluster);
        RouteHandlerBuilder deleteCluster = routes.MapDelete("/{uniqueName}", DeleteCluster);
        RouteHandlerBuilder forgetCluster = routes.MapDelete("/{uniqueName}/registration", ForgetCluster);
        RouteHandlerBuilder prepareDeployment = routes.MapPost("/{uniqueName}/deployment-preparation", PrepareDeployment);
        RouteHandlerBuilder recoverCluster = routes.MapPost("/{uniqueName}/recover", (string uniqueName, ClusterRecoveryRequest request,
            HttpContext context, [FromServices] ClusterDeploymentService deployments, CancellationToken token) => RunBackup(uniqueName, context,
                () => deployments.RecoverClusterAsync(uniqueName, request.Generation, context.Request.Headers["Idempotency-Key"].ToString(),
                    context.User.Identity?.Name ?? "anonymous", token)));
        RouteHandlerBuilder recoverDeployment = routes.MapPost("/{uniqueName}/gateway/recover", RecoverDeployment);
        RouteHandlerBuilder activateDeployment = routes.MapPut("/{uniqueName}/deployment", ActivateDeployment);
        routes.MapGet("/{uniqueName}/deployment", (string uniqueName, HttpContext context, ClusterCatalog catalog) =>
        {
            SetProtocolHeader(context);
            if (!context.User.CanQueryCluster(uniqueName)) return Error(403, "cluster_forbidden", "The credential cannot access this cluster.");
            var cluster = catalog.GetCluster(uniqueName);
            return cluster is null ? Error(404, "unknown_cluster", "Cluster was not found.")
                : Results.Json(Envelope(cluster.ActiveDeployment), JsonOptions);
        });
        RouteHandlerBuilder stageDependencies = routes.MapPut("/{uniqueName}/dependencies", StageDependencies);
        RouteHandlerBuilder submitCommand = routes.MapPost("/{uniqueName}/commands", SubmitCommand);
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
            submitCommand.RequireAuthorization(QuasarPolicyNames.ClusterManage);
            stagePackage.RequireAuthorization(QuasarPolicyNames.ClusterManage);
            selectPackage.RequireAuthorization(QuasarPolicyNames.ClusterManage);
            stageDependencies.RequireAuthorization(QuasarPolicyNames.ClusterManage);
            activateDeployment.RequireAuthorization(QuasarPolicyNames.ClusterManage);
            recoverCluster.RequireAuthorization(QuasarPolicyNames.ClusterManage);
            recoverDeployment.RequireAuthorization(QuasarPolicyNames.ClusterManage);
            prepareDeployment.RequireAuthorization(QuasarPolicyNames.ClusterManage);
            createCluster.RequireAuthorization(QuasarPolicyNames.ClusterManage);
            deleteCluster.RequireAuthorization(QuasarPolicyNames.ClusterManage);
            forgetCluster.RequireAuthorization(QuasarPolicyNames.ClusterManage);
            captureBackup.RequireAuthorization(QuasarPolicyNames.ClusterManage);
            archiveExport.RequireAuthorization(QuasarPolicyNames.ClusterManage);
            beginUpdate.RequireAuthorization(QuasarPolicyNames.ClusterManage);
            restoreBackup.RequireAuthorization(QuasarPolicyNames.ClusterManage);
            deploymentInputs.RequireAuthorization(QuasarPolicyNames.ClusterManage);
            setConfig.RequireAuthorization(QuasarPolicyNames.ClusterManage);
            setGoal.RequireAuthorization(QuasarPolicyNames.ClusterManage);
            setGatewaySpec.RequireAuthorization(QuasarPolicyNames.ClusterManage);
            restartGateway.RequireAuthorization(QuasarPolicyNames.ClusterManage);
            applyAttachment.RequireAuthorization(QuasarPolicyNames.ClusterManage);
            applyGateway.RequireAuthorization(QuasarPolicyNames.ClusterManage);
        }
    }

    internal static async Task<IResult> GetPackageRelease(string uniqueName, HttpContext context,
        ClusterCatalog catalog, [FromServices] ClusterPackageService packages, CancellationToken token)
    {
        SetProtocolHeader(context);
        if (catalog.GetCluster(uniqueName) is null)
            return Error(404, "unknown_cluster", "Cluster was not found.");
        if (!context.User.CanQueryCluster(uniqueName))
            return Error(403, "cluster_forbidden", "The credential cannot access this cluster.");
        try { return Results.Json(Envelope(await packages.GetReleaseAsync(null, token)), JsonOptions); }
        catch (Exception error) when (IsPackageError(error, token))
        { return Error(502, "cluster_package_unavailable", error is ClusterPackageException ? error.Message
            : "Could not read a valid cluster release from GitHub repository CometWorks/cluster."); }
    }

    private static async Task<IResult> StagePackage(string uniqueName, ClusterPackageRequest request,
        HttpContext context, ClusterCatalog catalog, [FromServices] ClusterPackageService packages,
        ClusterOperationStore operations, CancellationToken token)
    {
        SetProtocolHeader(context);
        if (catalog.GetCluster(uniqueName) is null)
            return Error(404, "unknown_cluster", "Cluster was not found.");
        if (!context.User.CanQueryCluster(uniqueName))
            return Error(403, "cluster_forbidden", "The credential cannot access this cluster.");
        try
        {
            var operation = await operations.ExecuteAsync(uniqueName, "cluster.package.stage",
                context.Request.Headers["Idempotency-Key"].ToString(), context.User.Identity?.Name ?? "anonymous",
                request, async cancellation =>
                {
                    try { return Envelope(await packages.StageAsync(request, cancellation)); }
                    catch (Exception error) when (IsPackageError(error, cancellation))
                    {
                        throw new ClusterPackageException(error is ClusterPackageException or InvalidDataException or PlatformNotSupportedException
                            ? error.Message : "Cluster package staging failed. Check GitHub access and local storage.");
                    }
                }, token);
            return AcceptedOperation(uniqueName, context, operation);
        }
        catch (ClusterOperationConflictException error) { return Error(error.StatusCode, error.Code, error.Message); }
        catch (ClusterOperationStoreUnavailableException) { return Error(503, "operation_store_unavailable", "Cluster operation store is unavailable."); }
    }

    private static bool IsPackageError(Exception error, CancellationToken token) =>
        error is ClusterPackageException or IOException or InvalidDataException or UnauthorizedAccessException or HttpRequestException
            or JsonException or System.Xml.XmlException or KeyNotFoundException or InvalidOperationException or PlatformNotSupportedException or FormatException
        || (error is OperationCanceledException && !token.IsCancellationRequested);

    internal static async Task<IResult> GetDependencyCandidate(string uniqueName, HttpContext context,
        ClusterCatalog catalog, [FromServices] ClusterDependencyService dependencies, CancellationToken token)
    {
        SetProtocolHeader(context);
        var cluster = catalog.GetCluster(uniqueName);
        if (cluster is null) return Error(404, "unknown_cluster", "Cluster was not found.");
        if (!context.User.CanQueryCluster(uniqueName))
            return Error(403, "cluster_forbidden", "The credential cannot access this cluster.");
        try { return Results.Json(Envelope(await dependencies.InspectAsync(cluster, token)), JsonOptions); }
        catch (Exception error) when (IsPackageError(error, token))
        { return Error(409, "cluster_dependencies_unavailable", error is InvalidDataException ? error.Message
            : "Could not inspect installed dependency inputs. Check package selection and configured dependency paths."); }
    }

    internal static async Task<IResult> GetDependencies(string uniqueName, HttpContext context,
        ClusterCatalog catalog, [FromServices] ClusterDependencyService dependencies, CancellationToken token)
    {
        SetProtocolHeader(context);
        var cluster = catalog.GetCluster(uniqueName);
        if (cluster is null) return Error(404, "unknown_cluster", "Cluster was not found.");
        if (!context.User.CanQueryCluster(uniqueName))
            return Error(403, "cluster_forbidden", "The credential cannot access this cluster.");
        string? errorCode = null;
        ClusterDependencyCandidate? verified = null;
        if (cluster.DependencyManifestSha256 is { } hash)
        {
            try { verified = await dependencies.VerifyAsync(cluster, hash, token); }
            catch (Exception error) when (IsPackageError(error, token)) { errorCode = "cluster_dependencies_invalid"; }
        }
        return Results.Json(Envelope(new ClusterDependencyStatus(cluster.PackageSelection?.Revision ?? 0,
            cluster.DependencyManifestSha256, verified is not null, errorCode, verified)), JsonOptions);
    }

    internal static async Task<IResult> GetDeploymentInputs(string uniqueName, HttpContext context,
        ClusterCatalog catalog, [FromServices] ClusterDependencyService dependencies, CancellationToken token)
    {
        SetProtocolHeader(context);
        var cluster = catalog.GetCluster(uniqueName);
        if (cluster is null) return Error(404, "unknown_cluster", "Cluster was not found.");
        if (!context.User.CanQueryCluster(uniqueName))
            return Error(403, "cluster_forbidden", "The credential cannot access this cluster.");
        try { return Results.Json(Envelope(await dependencies.GetDeploymentInputsAsync(cluster, token)), JsonOptions); }
        catch (Exception error) when (IsPackageError(error, token))
        { return Error(409, "cluster_deployment_inputs_unavailable", "The selected package and dependency snapshot must verify before deployment."); }
    }

    internal static async Task<IResult> ActivateDeployment(string uniqueName, ClusterDeploymentRequest request,
        HttpContext context, [FromServices] ClusterDeploymentService deployments, CancellationToken token)
    {
        SetProtocolHeader(context);
        if (!context.User.CanQueryCluster(uniqueName)) return Error(403, "cluster_forbidden", "The credential cannot access this cluster.");
        try
        {
            var operation = await deployments.ActivateAsync(uniqueName, request,
                context.Request.Headers["Idempotency-Key"].ToString(), context.User.Identity?.Name ?? "anonymous", token);
            return AcceptedOperation(uniqueName, context, operation);
        }
        catch (ClusterOperationConflictException error) { return Error(error.StatusCode, error.Code, error.Message); }
        catch (ClusterOperationStoreUnavailableException)
        { return Error(503, "operation_store_unavailable", "Cluster operation store is unavailable."); }
        catch (Exception error) when (error is InvalidOperationException or InvalidDataException or ArgumentException)
        { return Error(409, "deployment_conflict", error.Message); }
        catch (KeyNotFoundException) { return Error(404, "unknown_cluster", "Cluster was not found."); }
    }

    private static async Task<IResult> BeginUpdate(string uniqueName, ClusterUpdateRequest request, HttpContext context,
        [FromServices] ClusterUpdateService updates, CancellationToken token)
    {
        SetProtocolHeader(context);
        if (!context.User.CanQueryCluster(uniqueName)) return Error(403, "cluster_forbidden", "The credential cannot access this cluster.");
        try { return AcceptedOperation(uniqueName, context, await updates.BeginAsync(uniqueName, request,
            context.Request.Headers["Idempotency-Key"].ToString(), context.User.Identity?.Name ?? "anonymous", token)); }
        catch (ClusterOperationConflictException error) { return Error(error.StatusCode, error.Code, error.Message); }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException or ArgumentException or IOException)
        { return Error(409, "update_conflict", error.Message); }
        catch (KeyNotFoundException) { return Error(404, "unknown_cluster", "Cluster was not found."); }
    }

    private static Task<IResult> CaptureBackup(string uniqueName, ClusterBackupRequest request, HttpContext context,
        [FromServices] ClusterBackupService backups, CancellationToken token) => RunBackup(uniqueName, context,
            () => backups.CaptureAsync(uniqueName, request, context.Request.Headers["Idempotency-Key"].ToString(),
                context.User.Identity?.Name ?? "anonymous", token));

    private static Task<IResult> RestoreBackup(string uniqueName, ClusterRestoreRequest request, HttpContext context,
        [FromServices] ClusterBackupService backups, CancellationToken token) => RunBackup(uniqueName, context,
            () => backups.RestoreAsync(uniqueName, request, context.Request.Headers["Idempotency-Key"].ToString(),
                context.User.Identity?.Name ?? "anonymous", token));

    private static async Task<IResult> RunBackup(string uniqueName, HttpContext context, Func<Task<ClusterOperation>> execute)
    {
        SetProtocolHeader(context);
        if (!context.User.CanQueryCluster(uniqueName)) return Error(403, "cluster_forbidden", "The credential cannot access this cluster.");
        try { return AcceptedOperation(uniqueName, context, await execute()); }
        catch (ClusterOperationConflictException error) { return Error(error.StatusCode, error.Code, error.Message); }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException or ArgumentException or IOException)
        { return Error(409, "backup_conflict", error.Message); }
        catch (KeyNotFoundException) { return Error(404, "unknown_cluster", "Cluster was not found."); }
    }

    private static async Task<IResult> ForgetCluster(string uniqueName, string confirmation, HttpContext context,
        [FromServices] ClusterDeploymentService deployments, CancellationToken token)
    {
        SetProtocolHeader(context);
        if (!context.User.CanQueryCluster(uniqueName)) return Error(403, "cluster_forbidden", "Cluster access denied.");
        try { await deployments.ForgetAsync(uniqueName, confirmation, token); return Results.NoContent(); }
        catch (KeyNotFoundException) { return Error(404, "unknown_cluster", "Cluster was not found."); }
        catch (Exception error) when (error is InvalidOperationException or IOException)
        { return Error(409, "cluster_forget_conflict", error.Message); }
    }

    private static async Task<IResult> DeleteCluster(string uniqueName, HttpContext context,
        [FromServices] ClusterDeploymentService deployments, CancellationToken token)
    {
        SetProtocolHeader(context);
        if (!context.User.CanQueryCluster(uniqueName)) return Error(403, "cluster_forbidden", "Cluster access denied.");
        try
        {
            await deployments.DeleteAsync(uniqueName, token);
            return Results.NoContent();
        }
        catch (KeyNotFoundException) { return Error(404, "unknown_cluster", "Cluster was not found."); }
        catch (ClusterHostException error) { return Error(409, "cluster_delete_unverified", error.Message); }
        catch (Exception error) when (error is InvalidOperationException or IOException)
        { return Error(409, "cluster_delete_conflict", error.Message); }
    }

    private static async Task<IResult> CreateCluster(ClusterCreateRequest request, HttpContext context,
        ClusterCatalog catalog, CancellationToken token)
    {
        SetProtocolHeader(context);
        if (!context.User.CanQueryCluster(request.UniqueName)) return Error(403, "cluster_forbidden", "The credential cannot access this cluster.");
        try { return Results.Json(Envelope(await catalog.CreateAsync(request, token)), JsonOptions); }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException or ArgumentException)
        { return Error(409, "cluster_create_conflict", error.Message); }
    }

    private static async Task<IResult> PrepareDeployment(string uniqueName, ClusterPreparationRequest request,
        HttpContext context, [FromServices] ClusterDeploymentService deployments, CancellationToken token)
    {
        SetProtocolHeader(context);
        if (!context.User.CanQueryCluster(uniqueName)) return Error(403, "cluster_forbidden", "The credential cannot access this cluster.");
        try
        {
            return AcceptedOperation(uniqueName, context, await deployments.PrepareAsync(uniqueName, request,
                context.Request.Headers["Idempotency-Key"].ToString(), context.User.Identity?.Name ?? "anonymous", token));
        }
        catch (ClusterOperationConflictException error) { return Error(error.StatusCode, error.Code, error.Message); }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException or ArgumentException or JsonException)
        { return Error(409, "preparation_conflict", error.Message); }
        catch (KeyNotFoundException) { return Error(404, "unknown_cluster", "Cluster or required specification field was not found."); }
    }

    private static async Task<IResult> RecoverDeployment(string uniqueName, HttpContext context,
        [FromServices] ClusterDeploymentService deployments, CancellationToken token)
    {
        SetProtocolHeader(context);
        if (!context.User.CanQueryCluster(uniqueName)) return Error(403, "cluster_forbidden", "The credential cannot access this cluster.");
        try
        {
            return AcceptedOperation(uniqueName, context, await deployments.RecoverGatewayAsync(uniqueName,
                context.Request.Headers["Idempotency-Key"].ToString(), context.User.Identity?.Name ?? "anonymous", token));
        }
        catch (ClusterOperationConflictException error) { return Error(error.StatusCode, error.Code, error.Message); }
        catch (InvalidOperationException error) { return Error(409, "recovery_conflict", error.Message); }
        catch (KeyNotFoundException) { return Error(404, "unknown_cluster", "Cluster was not found."); }
    }

    internal static async Task<IResult> StageDependencies(string uniqueName, ClusterDependencyRequest request,
        HttpContext context, ClusterCatalog catalog, [FromServices] ClusterDependencyService dependencies,
        ClusterOperationStore operations, CancellationToken token)
    {
        SetProtocolHeader(context);
        var cluster = catalog.GetCluster(uniqueName);
        if (cluster is null) return Error(404, "unknown_cluster", "Cluster was not found.");
        if (!context.User.CanQueryCluster(uniqueName))
            return Error(403, "cluster_forbidden", "The credential cannot access this cluster.");
        try { ClusterDependencyService.ValidateRequest(request); }
        catch (InvalidDataException error) { return Error(400, "invalid_dependency_selection", error.Message); }
        try
        {
            var operation = await operations.ExecuteAsync(uniqueName, "cluster.dependencies.stage",
                context.Request.Headers["Idempotency-Key"].ToString(), context.User.Identity?.Name ?? "anonymous",
                request, async cancellation =>
                {
                    try
                    {
                        var staged = await dependencies.StageAsync(cluster, request, cancellation);
                        await catalog.SelectDependenciesAsync(uniqueName, request, cancellation);
                        return Envelope(staged);
                    }
                    catch (Exception error) when (IsPackageError(error, cancellation))
                    { throw new ClusterPackageException(error is InvalidDataException ? error.Message
                        : "Dependency provisioning failed. Check configured inputs and local storage."); }
                }, token);
            return AcceptedOperation(uniqueName, context, operation);
        }
        catch (ClusterOperationConflictException error) { return Error(error.StatusCode, error.Code, error.Message); }
        catch (ClusterOperationStoreUnavailableException)
        { return Error(503, "operation_store_unavailable", "Cluster operation store is unavailable."); }
    }

    internal static async Task<IResult> GetPackageSelection(string uniqueName, HttpContext context,
        ClusterCatalog catalog, [FromServices] ClusterPackageService packages, CancellationToken token)
    {
        SetProtocolHeader(context);
        var cluster = catalog.GetCluster(uniqueName);
        if (cluster is null) return Error(404, "unknown_cluster", "Cluster was not found.");
        if (!context.User.CanQueryCluster(uniqueName))
            return Error(403, "cluster_forbidden", "The credential cannot access this cluster.");
        var selected = cluster.PackageSelection;
        string? errorCode = null;
        if (selected is not null)
        {
            try
            {
                var installed = await packages.GetInstalledAsync(new(selected.Version, selected.Sha256), token);
                if (installed.Commit != selected.Commit) errorCode = "cluster_package_invalid";
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
            { errorCode = "cluster_package_missing"; }
            catch (Exception error) when (IsPackageError(error, token))
            { errorCode = "cluster_package_invalid"; }
        }
        return Results.Json(Envelope(new ClusterPackageSelectionStatus(selected?.Revision ?? 0, selected,
            selected is not null && errorCode is null, errorCode)), JsonOptions);
    }

    internal static async Task<IResult> SelectPackage(string uniqueName, ClusterPackageSelectionRequest request,
        HttpContext context, ClusterCatalog catalog, [FromServices] ClusterPackageService packages,
        ClusterOperationStore operations, CancellationToken token)
    {
        SetProtocolHeader(context);
        if (catalog.GetCluster(uniqueName) is null)
            return Error(404, "unknown_cluster", "Cluster was not found.");
        if (!context.User.CanQueryCluster(uniqueName))
            return Error(403, "cluster_forbidden", "The credential cannot access this cluster.");
        if (request.ExpectedRevision is null or < 0 or long.MaxValue)
            return Error(400, "package_revision_required", "A non-negative expectedRevision is required.");
        try
        {
            ClusterPackageService.ValidateVersion(request.Version);
            if (!ClusterPackageService.IsHash(request.Sha256, 64))
                throw new InvalidDataException("The selected archive SHA-256 is required.");
        }
        catch (InvalidDataException error) { return Error(400, "invalid_package_selection", error.Message); }
        string key = context.Request.Headers["Idempotency-Key"].ToString();
        try
        {
            var operation = await operations.ExecuteAsync(uniqueName, "cluster.package.select", key,
                context.User.Identity?.Name ?? "anonymous", request, async cancellation =>
                {
                    try
                    {
                        var installed = await packages.GetInstalledAsync(new(request.Version, request.Sha256), cancellation);
                        return Envelope(await catalog.SelectPackageAsync(uniqueName, request.ExpectedRevision.Value,
                            installed, key, cancellation));
                    }
                    catch (Exception error) when (IsPackageError(error, cancellation))
                    {
                        throw new ClusterPackageException(error is InvalidDataException ? error.Message
                            : "Could not verify or select the staged package. Check local package and catalog storage.");
                    }
                }, token);
            return AcceptedOperation(uniqueName, context, operation);
        }
        catch (ClusterOperationConflictException error) { return Error(error.StatusCode, error.Code, error.Message); }
        catch (ClusterOperationStoreUnavailableException)
        { return Error(503, "operation_store_unavailable", "Cluster operation store is unavailable."); }
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

    private static Task<IResult> RestartGateway(string uniqueName, HttpContext context,
        ClusterCatalog catalog, ClusterCommandService commands, CancellationToken token) =>
        SubmitCommand(uniqueName, new ClusterAdminCommand("gateway-restart"), context, catalog, commands, token);

    private static async Task<IResult> SubmitCommand(string uniqueName, [FromBody] ClusterAdminCommand command,
        HttpContext context, ClusterCatalog catalog, ClusterCommandService commands, CancellationToken token)
    {
        SetProtocolHeader(context);
        if (catalog.GetCluster(uniqueName) is null)
            return Error(404, "unknown_cluster", "Cluster was not found.");
        if (!context.User.CanQueryCluster(uniqueName))
            return Error(403, "cluster_forbidden", "The credential cannot access this cluster.");
        try
        {
            var operation = await commands.SubmitAsync(uniqueName, command,
                context.Request.Headers["Idempotency-Key"].ToString(), context.User.Identity?.Name ?? "anonymous", token);
            return AcceptedOperation(uniqueName, context, operation);
        }
        catch (ClusterOperationConflictException error) { return Error(error.StatusCode, error.Code, error.Message); }
        catch (ClusterOperationStoreUnavailableException error) { return Error(503, "operation_store_unavailable", error.Message); }
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

    private static Task<IResult> SetPolicy(string uniqueName, Admin.AdminConfigUpdate policy,
        HttpContext context, ClusterCatalog catalog, ClusterCommandService commands, CancellationToken token) =>
        SubmitCommand(uniqueName, ClusterAdminCommand.Create("config-set", policy), context, catalog, commands, token);

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
                context.User.Identity?.Name ?? "anonymous", attachment, token => catalog.WithLifecycleAsync(uniqueName, async current =>
                {
                    EnsureDirectHostMutationAllowed(current);
                    await catalog.RecordShutdownProofAsync(current, null, token);
                    HostContract.HostEnvelope<HostContract.HostAttachmentStatus> result =
                        await client.ApplyAttachmentAsync(current, attachment, token);
                    return new Admin.AdminEnvelope<HostContract.HostAttachmentStatus>(
                        Admin.AdminProtocol.Version, result.CapturedAt, result.Data);
                }, token), cancellationToken);
            context.Response.Headers.Location = $"/api/v1/clusters/{Uri.EscapeDataString(uniqueName)}"
                + $"/operations/{operation.OperationId}";
            return Results.Json(Envelope(operation), JsonOptions, statusCode: operation.State == ClusterOperationState.Running
            ? StatusCodes.Status202Accepted : StatusCodes.Status200OK);
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
                context.User.Identity?.Name ?? "anonymous", gateway, token =>
                catalog.WithLifecycleAsync(uniqueName, async current =>
                {
                    EnsureDirectHostMutationAllowed(current);
                    var effectiveGateway = gateway;
                    if (gateway.Goal == HostContract.GatewayGoal.On && operations.HasPendingShutdown(uniqueName))
                        throw new ClusterOperationConflictException(409, "shutdown_pending",
                            "Wait for the pending shutdown before starting the Gateway.");
                    if (gateway.Goal == HostContract.GatewayGoal.Off)
                    {
                        var host = (await client.GetStatusAsync(current, token)).Data;
                        var observed = host.Gateways?.FirstOrDefault(item =>
                            item.ClusterId.Equals(uniqueName, StringComparison.OrdinalIgnoreCase));
                        if (!host.GatewayStopFencing || observed?.ProcessId is not { } pid
                            || observed.LaunchedAt is not { } launchedAt || !ClusterReconciler.MatchesSpec(observed, gateway))
                            throw new ClusterOperationConflictException(409, "gateway_stop_fencing_unavailable",
                                "Host must report a matching Gateway process and support fenced stops.");
                        effectiveGateway = gateway with { StopFence = new(pid, launchedAt) };
                        Admin.ClusterStatus status = (await gatewayClient.GetStatusAsync(current, token)).Data;
                        EnsureGatewayCanStop(status);
                        if (!string.Equals(status.ClusterId, uniqueName, StringComparison.OrdinalIgnoreCase))
                            throw new ClusterOperationConflictException(409, "cluster_identity_mismatch",
                                "Gateway status belongs to a different cluster.");
                    }
                    // A direct Host action bypasses goal reconciliation; discard its old
                    // shutdown proof before any side effect, including a lost response.
                    await catalog.RecordShutdownProofAsync(current, null, token);
                    HostContract.HostEnvelope<HostContract.GatewayStatus> result =
                        await client.ApplyGatewayAsync(current, effectiveGateway, token);
                    return new Admin.AdminEnvelope<HostContract.GatewayStatus>(
                        Admin.AdminProtocol.Version, result.CapturedAt, result.Data);
                }, token), cancellationToken);
            context.Response.Headers.Location = $"/api/v1/clusters/{Uri.EscapeDataString(uniqueName)}"
                + $"/operations/{operation.OperationId}";
            return Results.Json(Envelope(operation), JsonOptions, statusCode: operation.State == ClusterOperationState.Running
            ? StatusCodes.Status202Accepted : StatusCodes.Status200OK);
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

    internal static void EnsureDirectHostMutationAllowed(ClusterDefinition cluster)
    {
        if (cluster.ActiveDeployment is not null || cluster.Update is { Phase: not ClusterUpdatePhase.Complete }
            || cluster.PendingDeploymentHash is not null || cluster.PendingRestoreHash is not null)
            throw new ClusterOperationConflictException(409, "managed_deployment",
                "Use managed deployment, goal or recovery operations for this cluster.");
    }

    internal static void EnsureGatewayCanStop(Admin.ClusterStatus status)
    {
        if (status.Phase != Admin.ClusterPhase.Down || status.LastCleanShutdown is null
            || status.LastCleanShutdown < status.ShutdownStarted)
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
        return Results.Json(Envelope(operation), JsonOptions, statusCode: operation.State == ClusterOperationState.Running
            ? StatusCodes.Status202Accepted : StatusCodes.Status200OK);
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

public sealed record ClusterRecoveryRequest(Guid Generation);
