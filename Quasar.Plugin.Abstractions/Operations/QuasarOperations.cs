using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Quasar.Plugin.Abstractions.Operations;

public sealed record QuasarOperationContext(ClaimsPrincipal User, IServiceProvider Services)
{
    public QuasarOperationKind Kind { get; init; }
    public Stream? Upload { get; init; }
    public Func<CancellationToken, Task>? VerifyAccess { get; init; }
    public Task EnsureAuthorizedAsync(CancellationToken token) => VerifyAccess?.Invoke(token)
        ?? throw new InvalidOperationException("This operation has no authorization boundary.");
}

public enum QuasarOperationKind { Json, Read, Upload, Subscription }

public static class QuasarSdkLimits
{
    public const int MaximumJsonBytes = 64 * 1024;
    public const int MaximumJsonDepth = 32;
    public const int TransferBufferBytes = 64 * 1024;
    public static readonly TimeSpan AuthorizationRecheckInterval = TimeSpan.FromSeconds(1);
}

public sealed class QuasarOperationException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

public sealed record QuasarRead(Func<CancellationToken, Task<Stream>> OpenAsync, string ContentType,
    string? FileName = null, long? Length = null, bool EnableRanges = false,
    DateTimeOffset? LastModified = null, string? EntityTag = null,
    Func<CancellationToken, Task>? ValidateAsync = null);

public sealed record QuasarUpload<TResponse>(Func<CancellationToken, Task<TResponse>> CommitAsync);
public sealed record QuasarSubscription(IAsyncEnumerable<object?> Items);

/// <summary>HTTP compatibility adapter; plugins normally register handlers and use the browser SDK.</summary>
public interface IQuasarHttpOperations
{
    Task<IResult> ExecuteAsync(HttpContext http, string pluginId, string operationId, JsonElement? request = null);
}

public interface IQuasarOperationDispatcher
{
    Task<QuasarOperationResult> DispatchAsync(QuasarOperationContext context, string pluginId,
        string operationId, JsonElement request, CancellationToken cancellationToken);
}

public sealed record QuasarOperationResult(int StatusCode, object? Value = null, string? Error = null)
{
    public IResult ToHttpResult() => Error is null
        ? Results.Json(Value, statusCode: StatusCode)
        : Results.Problem(statusCode: StatusCode, detail: Error);
}

public sealed class QuasarOperationRule
{
    public bool Enabled { get; set; } = true;
    public List<string>? RequiredPolicies { get; set; } = [];
    public QuasarOperationRule Clone() => new() { Enabled = Enabled, RequiredPolicies = RequiredPolicies?.ToList() };
}

/// <summary>A registered application operation. Transport adapters must use the dispatcher.</summary>
public abstract class QuasarOperation
{
    private static readonly Regex Identifier = new("^[a-z0-9]+(?:[._-][a-z0-9]+)*$", RegexOptions.CultureInvariant);
    protected static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { MaxDepth = QuasarSdkLimits.MaximumJsonDepth };

    protected QuasarOperation(string pluginId, string operationId, IEnumerable<string> policies)
    {
        if (!IsIdentifier(pluginId) || !IsIdentifier(operationId))
            throw new ArgumentException("SDK plugin and operation IDs must be lowercase identifiers of at most 128 characters.");
        PluginId = pluginId;
        OperationId = operationId;
        DefaultPolicies = Array.AsReadOnly(policies.ToArray());
    }

    public string PluginId { get; }
    public string OperationId { get; }
    public string Key => PluginId + "/" + OperationId;
    public IReadOnlyList<string> DefaultPolicies { get; }
    public virtual QuasarOperationKind Kind => QuasarOperationKind.Json;
    public virtual long MaximumUploadBytes => 0;
    public static bool IsIdentifier(string? value) => value is { Length: > 0 and <= 128 } && Identifier.IsMatch(value);
    public abstract Task<object?> InvokeAsync(QuasarOperationContext context, JsonElement request, CancellationToken token);

    internal sealed class Typed<TRequest, TResponse>(string pluginId, string operationId,
        IEnumerable<string> policies, Func<QuasarOperationContext, TRequest, CancellationToken, Task<TResponse>> handler,
        QuasarOperationKind kind = QuasarOperationKind.Json, long maximumUploadBytes = 0)
        : QuasarOperation(pluginId, operationId, policies)
    {
        public override QuasarOperationKind Kind => kind;
        public override long MaximumUploadBytes => maximumUploadBytes;
        public override async Task<object?> InvokeAsync(QuasarOperationContext context, JsonElement request, CancellationToken token)
        {
            var value = request.Deserialize<TRequest>(JsonOptions);
            if (value is null) throw new JsonException("An operation request is required.");
            return await handler(context, value, token);
        }
    }
}

public static class QuasarOperationExtensions
{
    public static IServiceCollection AddQuasarOperation<TRequest, TResponse>(this IServiceCollection services,
        string pluginId, string operationId, IEnumerable<string> policies,
        Func<QuasarOperationContext, TRequest, CancellationToken, Task<TResponse>> handler)
    {
        services.AddSingleton<QuasarOperation>(new QuasarOperation.Typed<TRequest, TResponse>(pluginId, operationId, policies, handler));
        return services;
    }

    public static IServiceCollection AddQuasarRead<TRequest>(this IServiceCollection services,
        string pluginId, string operationId, IEnumerable<string> policies,
        Func<QuasarOperationContext, TRequest, CancellationToken, Task<QuasarRead>> handler)
    {
        services.AddSingleton<QuasarOperation>(new QuasarOperation.Typed<TRequest, QuasarRead>(pluginId, operationId, policies, handler, QuasarOperationKind.Read));
        return services;
    }

    public static IServiceCollection AddQuasarUpload<TRequest, TResponse>(this IServiceCollection services,
        string pluginId, string operationId, IEnumerable<string> policies, long maximumBytes,
        Func<QuasarOperationContext, TRequest, Stream, CancellationToken, Task<QuasarUpload<TResponse>>> prepare)
    {
        if (maximumBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        services.AddSingleton<QuasarOperation>(new QuasarOperation.Typed<TRequest, TResponse>(pluginId, operationId, policies,
            async (context, request, token) =>
            {
                var upload = await prepare(context, request, context.Upload ?? throw new InvalidOperationException("Upload body is missing."), token);
                await context.EnsureAuthorizedAsync(token);
                return await upload.CommitAsync(token);
            }, QuasarOperationKind.Upload, maximumBytes));
        return services;
    }

    public static IServiceCollection AddQuasarSubscription<TRequest, TItem>(this IServiceCollection services,
        string pluginId, string operationId, IEnumerable<string> policies,
        Func<QuasarOperationContext, TRequest, CancellationToken, IAsyncEnumerable<TItem>> subscribe)
    {
        services.AddSingleton<QuasarOperation>(new QuasarOperation.Typed<TRequest, QuasarSubscription>(pluginId, operationId, policies,
            (context, request, token) => Task.FromResult(new QuasarSubscription(Box(subscribe(context, request, token), token))),
            QuasarOperationKind.Subscription));
        return services;
    }

    private static async IAsyncEnumerable<object?> Box<T>(IAsyncEnumerable<T> source,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        await foreach (var item in source.WithCancellation(token)) yield return item;
    }

    /// <summary>Preserves a read-only HTTP alias while sharing SDK authorization and settings.</summary>
    public static RouteHandlerBuilder MapQuasarOperationRead(this IEndpointRouteBuilder endpoints,
        string pattern, string pluginId, string operationId) => endpoints.MapGet(pattern,
        (HttpContext http, IQuasarHttpOperations adapter) => adapter.ExecuteAsync(http, pluginId, operationId)).RequireAuthorization();

    public static RouteHandlerBuilder MapQuasarOperation(this IEndpointRouteBuilder endpoints, string pattern,
        string[] methods, string pluginId, string operationId, Func<HttpContext, JsonElement>? request = null) =>
        endpoints.MapMethods(pattern, methods, (HttpContext http, IQuasarHttpOperations adapter) =>
            adapter.ExecuteAsync(http, pluginId, operationId, request?.Invoke(http))).RequireAuthorization();
}
