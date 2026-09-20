using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Magnetar.Protocol.Runtime;
using Admin = CometWorks.ClusterGateway.AdminContract.V1;

namespace Quasar.Services;

public sealed class ClusterOperationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
    private readonly string _directory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<(string Cluster, string Kind, string Key), SemaphoreSlim> _localExecutionGates = new();
    private readonly ConcurrentDictionary<string, ClusterOperation> _operations = new(StringComparer.Ordinal);
    private readonly ILogger<ClusterOperationStore>? _logger;
    private volatile bool _ready = true;

    public ClusterOperationStore(ILogger<ClusterOperationStore> logger)
        : this(Path.Combine(MagnetarPaths.GetQuasarDirectory(), "Operations", "Clusters"), logger)
    {
    }

    public ClusterOperationStore(string directory, ILogger<ClusterOperationStore>? logger = null)
    {
        _directory = directory;
        _logger = logger;
        try
        {
            Directory.CreateDirectory(_directory);
            foreach (string path in Directory.EnumerateFiles(_directory, "*.json"))
                Load(path);
            // A local operation runs inside this process, so none can still be running at startup.
            // Pending Gateway mutations are different: they resume with their persisted identity.
            foreach (var orphan in _operations.Values.Where(o => o.State == ClusterOperationState.Running && o.GatewayRequest is null).ToArray())
            {
                var failed = orphan with { State = ClusterOperationState.Failed, UpdatedAt = DateTimeOffset.UtcNow,
                    Error = new("interrupted_by_restart", "Quasar stopped before this operation completed. Check the cluster state and submit it again with a new Idempotency-Key.") };
                File.WriteAllText(Path.Combine(_directory, failed.OperationId + ".json"), JsonSerializer.Serialize(failed, JsonOptions));
                _operations[failed.OperationId] = failed;
                _logger?.LogWarning("Cluster operation {OperationId} ({Kind}) of {Cluster} was interrupted by a restart and is now Failed.",
                    failed.OperationId, failed.Kind, failed.Cluster);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger?.LogError(exception, "Cluster operation store {Directory} is unavailable.", _directory);
            _ready = false;
        }
    }

    // One unreadable record (for example a zero-length file after power loss) must not
    // disable cluster management; it is set aside and named in the log instead.
    private void Load(string path)
    {
        try
        {
            ClusterOperation? operation = JsonSerializer.Deserialize<ClusterOperation>(File.ReadAllText(path), JsonOptions);
            if (operation == null || string.IsNullOrEmpty(operation.OperationId))
                throw new InvalidDataException("The record has no operation ID.");
            _operations[operation.OperationId] = operation;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            string quarantine = path + ".corrupt";
            try
            {
                File.Move(path, quarantine, overwrite: true);
                _logger?.LogError(exception, "Invalid cluster operation record {Path} was moved to {Quarantine} and ignored.", path, quarantine);
            }
            catch (Exception moveError) when (moveError is IOException or UnauthorizedAccessException)
            {
                _logger?.LogError(exception, "Invalid cluster operation record {Path} was ignored and could not be moved aside: {Reason}", path, moveError.Message);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger?.LogError(exception, "Cluster operation record {Path} could not be read and was ignored.", path);
        }
    }

    public bool IsReady => (_ready || Recover()) && Directory.Exists(_directory);

    // A transient write failure (disk full, read-only remount) must not need a restart to clear.
    private bool Recover()
    {
        string probe = Path.Combine(_directory, ".probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(probe, "probe");
            File.Delete(probe);
            _logger?.LogInformation("Cluster operation store {Directory} is writable again.", _directory);
            return _ready = true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public ClusterOperation? Get(string operationId) =>
        _operations.GetValueOrDefault(operationId);

    internal bool HasPendingOperations(string cluster) => _operations.Values.Any(operation =>
        operation.Cluster.Equals(cluster, StringComparison.OrdinalIgnoreCase)
        && operation.State == ClusterOperationState.Running);

    internal bool HasPendingShutdown(string cluster) => _operations.Values.Any(operation =>
        operation.Cluster.Equals(cluster, StringComparison.OrdinalIgnoreCase)
        && operation.State == ClusterOperationState.Running
        && operation.Kind is "cluster.lifecycle.shutdown" or "cluster.shutdown");

    // The lifecycle owner calls this only after verifying clean Down AND the matching
    // fenced Host stop. A remote operation may remain Running if its final reply was lost.
    internal async Task CompleteShutdownAsync(Quasar.Models.ClusterDefinition cluster, CancellationToken token)
    {
        var proof = cluster.ShutdownProof;
        if (cluster.GoalState != Quasar.Models.DedicatedServerGoalState.Off
            || proof is null || proof.LifecycleId != cluster.GetLifecycleId())
            throw new InvalidOperationException("Shutdown completion requires matching clean-shutdown proof.");
        await _gate.WaitAsync(token);
        try
        {
            foreach (var operation in _operations.Values.Where(o =>
                o.Cluster.Equals(cluster.UniqueName, StringComparison.OrdinalIgnoreCase)
                && o.State == ClusterOperationState.Running
                && o.Kind is "cluster.lifecycle.shutdown" or "cluster.shutdown"
                && o.GatewayRequest?.GatewayUrl == cluster.GatewayUrl).ToArray())
                await SaveAsync(operation with { State = ClusterOperationState.Succeeded,
                    UpdatedAt = DateTimeOffset.UtcNow, Error = null,
                    Result = JsonSerializer.SerializeToElement(new { phase = "Down",
                        confirmation = "clean-shutdown-proof", proof }, JsonOptions) }, token);
        }
        finally { _gate.Release(); }
    }

    internal async Task FenceGatewayOperationsForRestoreAsync(string cluster, CancellationToken token, bool recovery = false)
    {
        await _gate.WaitAsync(token);
        try
        {
            foreach (var operation in _operations.Values.Where(o => o.Cluster == cluster
                && o.State == ClusterOperationState.Running && o.GatewayRequest is not null).ToArray())
                await SaveAsync(operation with { State = ClusterOperationState.Failed, UpdatedAt = DateTimeOffset.UtcNow,
                    Error = recovery ? new("superseded_by_recovery", "Explicit recovery fenced this earlier Gateway operation.")
                        : new("superseded_by_restore", "Explicit restore fenced this pre-restore Gateway operation.") }, token);
        }
        finally { _gate.Release(); }
    }

    public async Task<ClusterOperation> ExecuteAsync<TRequest, TResult>(string cluster, string kind,
        string idempotencyKey, string actor, TRequest request,
        Func<CancellationToken, Task<Admin.AdminEnvelope<TResult>>> execute,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 128)
            throw new ClusterOperationConflictException(StatusCodes.Status400BadRequest, "idempotency_key_required",
                "Idempotency-Key is required and cannot exceed 128 characters.");
        if (!IsReady)
            throw new ClusterOperationStoreUnavailableException();
        string requestHash = Convert.ToHexString(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions))).ToLowerInvariant();
        string key = idempotencyKey.Trim();

        // Downloads can take minutes. Serialize retries of one local operation without
        // holding up Gateway administration or unrelated local operations.
        var executionGate = _localExecutionGates.GetOrAdd((cluster.ToUpperInvariant(), kind, key), _ => new(1, 1));
        await executionGate.WaitAsync(cancellationToken);
        try
        {
            ClusterOperation? existing = _operations.Values.FirstOrDefault(operation =>
                operation.Cluster.Equals(cluster, StringComparison.OrdinalIgnoreCase)
                && operation.Kind == kind && operation.IdempotencyKey == key);
            if (existing != null)
            {
                if (existing.RequestHash != requestHash)
                    throw new ClusterOperationConflictException(StatusCodes.Status409Conflict, "idempotency_key_conflict",
                        "Idempotency-Key is already bound to different request content.");
                if (existing.State != ClusterOperationState.Running)
                    return existing;
            }
            else
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;
                existing = new ClusterOperation(Guid.NewGuid().ToString("N"), cluster, kind, key,
                    requestHash, actor, ClusterOperationState.Running, now, now, null, null);
                await PersistAsync(existing, cancellationToken);
                _operations[existing.OperationId] = existing;
            }

            try
            {
                Admin.AdminEnvelope<TResult> result = await execute(cancellationToken);
                existing = existing with
                {
                    State = ClusterOperationState.Succeeded,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    Result = JsonSerializer.SerializeToElement(result.Data, JsonOptions),
                };
            }
            catch (ClusterGatewayException exception)
            {
                existing = existing with
                {
                    State = ClusterOperationState.Failed,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    Error = new ClusterOperationError(exception.Code, exception.Message),
                };
            }
            catch (ClusterHostException exception)
            {
                existing = existing with
                {
                    State = ClusterOperationState.Failed,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    Error = new ClusterOperationError(exception.Code, exception.Message),
                };
            }
            catch (ClusterPackageException exception)
            {
                existing = existing with
                {
                    State = ClusterOperationState.Failed,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    Error = new ClusterOperationError("cluster_package_failed", exception.Message),
                };
            }
            catch (ClusterOperationConflictException exception)
            {
                // A conflict from the mutation itself is terminal. Admission/key conflicts above
                // still return HTTP errors without creating a durable operation.
                existing = existing with
                {
                    State = ClusterOperationState.Failed,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    Error = new ClusterOperationError(exception.Code, exception.Message),
                };
            }
            // Any other failure, a client disconnect included, must still close the record: a
            // record left Running blocks cluster deletion forever. The caller sees the exception.
            catch (Exception exception)
            {
                existing = existing with
                {
                    State = ClusterOperationState.Failed,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    Error = exception is OperationCanceledException
                        ? new ClusterOperationError("operation_cancelled", "The operation was cancelled before it completed.")
                        : new ClusterOperationError("operation_failed", exception.Message),
                };
                _operations[existing.OperationId] = existing;
                await PersistAsync(existing, CancellationToken.None);
                throw;
            }
            _operations[existing.OperationId] = existing;
            await PersistAsync(existing, CancellationToken.None);
            return existing;
        }
        finally
        {
            executionGate.Release();
        }
    }

    public async Task<ClusterOperation> ExecuteGatewayAsync(Quasar.Models.ClusterDefinition cluster,
        string kind, string method, string route, object? request, string idempotencyKey, string actor,
        ClusterGatewayClient client, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 128)
            throw new ClusterOperationConflictException(400, "idempotency_key_required", "Idempotency-Key is required (maximum 128 characters).");
        if (!IsReady) throw new ClusterOperationStoreUnavailableException();
        var pending = new GatewayOperationRequest(cluster.GatewayUrl, method, route,
            JsonSerializer.SerializeToElement(request, JsonOptions));
        string hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(pending, JsonOptions)));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var operation = _operations.Values.FirstOrDefault(o =>
                o.Cluster.Equals(cluster.UniqueName, StringComparison.OrdinalIgnoreCase)
                && o.Kind == kind && o.IdempotencyKey == idempotencyKey.Trim());
            if (operation is not null && operation.RequestHash != hash)
                throw new ClusterOperationConflictException(409, "idempotency_key_conflict", "The key is bound to different request content.");
            if (operation is null)
            {
                var now = DateTimeOffset.UtcNow;
                operation = new ClusterOperation(Guid.NewGuid().ToString("N"), cluster.UniqueName, kind,
                    idempotencyKey.Trim(), hash, actor, ClusterOperationState.Running, now, now, null, null,
                    GatewayRequest: pending);
                await SaveAsync(operation, cancellationToken);
            }
            return await ResumeGatewayAsync(operation, cluster, client, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task ReconcileGatewayAsync(ClusterCatalog catalog, ClusterGatewayClient client,
        CancellationToken cancellationToken)
    {
        if (!IsReady) return;
        foreach (var pending in _operations.Values.Where(o => o.State == ClusterOperationState.Running && o.GatewayRequest is not null))
        {
            if (catalog.GetCluster(pending.Cluster) is null) continue;
            await catalog.WithLifecycleAsync(pending.Cluster, async cluster =>
            {
                await _gate.WaitAsync(cancellationToken);
                try { return await ResumeGatewayAsync(_operations[pending.OperationId], cluster, client, cancellationToken); }
                finally { _gate.Release(); }
            }, cancellationToken);
        }
    }

    private async Task<ClusterOperation> ResumeGatewayAsync(ClusterOperation operation,
        Quasar.Models.ClusterDefinition cluster, ClusterGatewayClient client, CancellationToken token)
    {
        if (operation.State != ClusterOperationState.Running) return operation;
        var request = operation.GatewayRequest!;
        if (!request.GatewayUrl.Equals(cluster.GatewayUrl, StringComparison.Ordinal))
            return await SaveAsync(operation with { Error = new("gateway_changed", "Restore the original Gateway URL to resume this operation.") }, token);
        try
        {
            Admin.AdminOperation remote;
            if (operation.GatewayOperationId is { } id)
                remote = (await client.GetOperationAsync(cluster, id, token)).Data;
            else
            {
                remote = (await client.MutateAsync(cluster, request.Route, new HttpMethod(request.Method),
                    request.Body, "quasar-" + operation.OperationId, token)).Data;
            }
            if (remote.IdempotencyKey != "quasar-" + operation.OperationId
                || (operation.GatewayOperationId is { } expectedId && remote.OperationId != expectedId))
                throw new ClusterGatewayException(System.Net.HttpStatusCode.BadGateway, "protocol_mismatch", "Gateway returned a different operation identity.");
            operation = operation with
            {
                GatewayOperationId = remote.OperationId,
                State = remote.State switch
                {
                    Admin.AdminOperationState.Succeeded => ClusterOperationState.Succeeded,
                    Admin.AdminOperationState.Failed => ClusterOperationState.Failed,
                    _ => ClusterOperationState.Running,
                },
                Result = JsonSerializer.SerializeToElement(remote, JsonOptions),
                Error = remote.Error is null ? null : new(remote.Error.Code, remote.Error.Message),
                UpdatedAt = DateTimeOffset.UtcNow,
            };
        }
        catch (ClusterGatewayException error)
        {
            // A timeout or unavailable Gateway leaves the outcome unknown. Resume with the
            // same persisted key/ID; never turn a lost response into a second mutation.
            bool retry = (int)error.StatusCode >= 500 || (int)error.StatusCode is 408 or 429
                || (operation.GatewayOperationId is not null && (int)error.StatusCode is 401 or 403);
            operation = operation with
            {
                State = retry ? ClusterOperationState.Running : ClusterOperationState.Failed,
                Error = new(error.Code, error.Message), UpdatedAt = DateTimeOffset.UtcNow,
            };
        }
        return await SaveAsync(operation, token);
    }

    private async Task<ClusterOperation> SaveAsync(ClusterOperation operation, CancellationToken token)
    {
        await PersistAsync(operation, token);
        _operations[operation.OperationId] = operation;
        return operation;
    }

    private async Task PersistAsync(ClusterOperation operation, CancellationToken cancellationToken)
    {
        try
        {
            await AtomicFileWriter.WriteTextAsync(Path.Combine(_directory, operation.OperationId + ".json"),
                JsonSerializer.Serialize(operation, JsonOptions), cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger?.LogError(exception, "Cluster operation {OperationId} could not be written to {Directory}.", operation.OperationId, _directory);
            _ready = false;
            throw;
        }
    }
}

public enum ClusterOperationState { Running, Succeeded, Failed }

public sealed record ClusterOperation(
    string OperationId,
    string Cluster,
    string Kind,
    string IdempotencyKey,
    string RequestHash,
    string Actor,
    ClusterOperationState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    JsonElement? Result,
    ClusterOperationError? Error,
    string? GatewayOperationId = null,
    GatewayOperationRequest? GatewayRequest = null);

public sealed record ClusterOperationError(string Code, string Message);

public sealed class ClusterPackageException(string message) : Exception(message);

public sealed class ClusterOperationConflictException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}

public sealed class ClusterOperationStoreUnavailableException : Exception
{
    public ClusterOperationStoreUnavailableException() : base("Cluster operation store is unavailable.")
    {
    }
}

public sealed record GatewayOperationRequest(string GatewayUrl, string Method, string Route, JsonElement Body);

public sealed class ClusterOperationReconciler(ClusterOperationStore operations, ClusterCatalog catalog,
    ClusterGatewayClient client, ILogger<ClusterOperationReconciler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        do
        {
            try { await operations.ReconcileGatewayAsync(catalog, client, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception error) { logger.LogWarning(error, "Cluster operation reconciliation failed."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
