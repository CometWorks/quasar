using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Quasar.Plugin.Abstractions.Operations;

public sealed record QuasarOperationContext(ClaimsPrincipal User, IServiceProvider Services);

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
    protected static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { MaxDepth = 32 };

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
    public static bool IsIdentifier(string? value) => value is { Length: > 0 and <= 128 } && Identifier.IsMatch(value);
    public abstract Task<object?> InvokeAsync(QuasarOperationContext context, JsonElement request, CancellationToken token);

    internal sealed class Typed<TRequest, TResponse>(string pluginId, string operationId,
        IEnumerable<string> policies, Func<QuasarOperationContext, TRequest, CancellationToken, Task<TResponse>> handler)
        : QuasarOperation(pluginId, operationId, policies)
    {
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

    /// <summary>Preserves a read-only HTTP alias while sharing SDK authorization and settings.</summary>
    public static RouteHandlerBuilder MapQuasarOperationRead(this IEndpointRouteBuilder endpoints,
        string pattern, string pluginId, string operationId) => endpoints.MapGet(pattern,
        async (HttpContext http, IQuasarOperationDispatcher dispatcher) =>
        {
            http.Response.Headers.CacheControl = "private, no-store";
            var result = await dispatcher.DispatchAsync(new(http.User, http.RequestServices), pluginId, operationId,
                JsonSerializer.SerializeToElement(new { }), http.RequestAborted);
            return result.ToHttpResult();
        }).RequireAuthorization();
}
