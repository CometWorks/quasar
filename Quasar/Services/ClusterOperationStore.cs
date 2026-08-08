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
    private readonly ConcurrentDictionary<string, ClusterOperation> _operations = new(StringComparer.Ordinal);
    private volatile bool _ready = true;

    public ClusterOperationStore() : this(Path.Combine(MagnetarPaths.GetQuasarDirectory(), "Operations", "Clusters"))
    {
    }

    public ClusterOperationStore(string directory)
    {
        _directory = directory;
        try
        {
            Directory.CreateDirectory(_directory);
            foreach (string path in Directory.EnumerateFiles(_directory, "*.json"))
            {
                ClusterOperation? operation = JsonSerializer.Deserialize<ClusterOperation>(File.ReadAllText(path), JsonOptions);
                if (operation == null || operation.OperationId.Length == 0)
                    throw new InvalidDataException($"Invalid cluster operation record '{path}'.");
                _operations[operation.OperationId] = operation;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or JsonException or InvalidDataException)
        {
            _ready = false;
        }
    }

    public bool IsReady => _ready && Directory.Exists(_directory);

    public ClusterOperation? Get(string operationId) =>
        _operations.GetValueOrDefault(operationId);

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

        await _gate.WaitAsync(cancellationToken);
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
                _operations[existing.OperationId] = existing;
                await PersistAsync(existing, cancellationToken);
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
            _operations[existing.OperationId] = existing;
            await PersistAsync(existing, cancellationToken);
            return existing;
        }
        finally
        {
            _gate.Release();
        }
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
    ClusterOperationError? Error);

public sealed record ClusterOperationError(string Code, string Message);

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
