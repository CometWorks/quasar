using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Quasar.Plugin.Abstractions.Operations;

namespace Quasar.Services.Plugins;

public static class QuasarSdkEndpoints
{
    public const string Prefix = "/_quasar/sdk/v1";
    public const int MaximumJsonBytes = QuasarSdkLimits.MaximumJsonBytes;

    public static Task WriteAuthorizationErrorAsync(HttpContext http, int status)
    {
        http.Response.Headers.CacheControl = "private, no-store";
        return Results.Problem(statusCode: status, detail: status == 401
            ? "Authentication is required." : "SDK access is forbidden.").ExecuteAsync(http);
    }

    public static void MapQuasarSdk(this WebApplication app)
    {
        var sdk = app.MapGroup(Prefix).RequireAuthorization();
        sdk.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "private, no-store";
            try { return await next(context); }
            catch (UnauthorizedAccessException) { return Results.Problem(statusCode: 403, detail: "Security administration permission is required."); }
            catch (ArgumentException) { return Results.Problem(statusCode: 400, detail: "Invalid SDK settings or request."); }
            catch (JsonException) { return Results.Problem(statusCode: 400, detail: "Invalid SDK JSON request."); }
            catch (AntiforgeryValidationException) { return Results.Problem(statusCode: 400, detail: "Invalid antiforgery token."); }
            catch (QuasarOperationException error) { return Results.Problem(statusCode: error.StatusCode, detail: error.Message); }
        });
        sdk.MapGet("/session", async (HttpContext http, IAntiforgery antiforgery, QuasarBrowserSessions sessions) =>
        {
            await sessions.BindHttpSessionAsync(http);
            var tokens = antiforgery.GetAndStoreTokens(http);
            return Results.Json(new { version = 1, requestToken = tokens.RequestToken, headerName = tokens.HeaderName });
        });
        sdk.MapPost("/operations/{pluginId}/{operationId}", async (string pluginId, string operationId,
            HttpContext http, IQuasarOperationDispatcher dispatcher, IAntiforgery antiforgery) =>
        {
            await antiforgery.ValidateRequestAsync(http);
            using var body = await ReadJsonAsync(http);
            if (body is null) return Results.Problem(statusCode: 413, detail: "SDK control requests are limited to 64 KiB.");
            return (await dispatcher.DispatchAsync(new(http.User, http.RequestServices), pluginId,
                operationId, body.RootElement, http.RequestAborted)).ToHttpResult();
        });
        sdk.MapPost("/resources/{pluginId}/{operationId}", async (string pluginId, string operationId,
            HttpContext http, QuasarOperationService operations, QuasarTransferRegistry transfers, IAntiforgery antiforgery, QuasarBrowserSessions sessions) =>
        {
            await antiforgery.ValidateRequestAsync(http);
            var operation = operations.Find(pluginId, operationId) ?? throw new QuasarOperationException(404, "SDK operation not found.");
            if (operation.Kind == QuasarOperationKind.Json) throw new QuasarOperationException(400, "Use a JSON operation request.");
            var denied = await operations.CheckAccessAsync(http.User, pluginId, operationId, operation.Kind);
            if (denied is not null) return denied.ToHttpResult();
            using var body = await ReadJsonAsync(http);
            if (body is null) return Results.Problem(statusCode: 413);
            var transfer = transfers.Create(await sessions.BindHttpSessionAsync(http), pluginId, operationId, operation.Kind, body.RootElement);
            return Results.Json(new { id = transfer.Id, kind = operation.Kind.ToString().ToLowerInvariant(),
                url = http.Request.PathBase + Prefix + "/resources/" + transfer.Id });
        });
        sdk.MapMethods("/resources/{id}", ["GET", "PUT"], async (string id, HttpContext http,
            QuasarTransferRegistry transfers, QuasarHttpOperations adapter, IAntiforgery antiforgery, QuasarBrowserSessions sessions) =>
        {
            var transfer = transfers.Get(await sessions.BindHttpSessionAsync(http), id, claim: false);
            if ((transfer.Kind == QuasarOperationKind.Upload) != HttpMethods.IsPut(http.Request.Method))
                throw new QuasarOperationException(405, "Wrong transfer method.");
            if (HttpMethods.IsPut(http.Request.Method)) await antiforgery.ValidateRequestAsync(http);
            transfers.Get(await sessions.BindHttpSessionAsync(http), id, claim: true);
            return adapter.Result(transfer);
        });
        sdk.MapDelete("/resources/{id}", async (string id, HttpContext http, QuasarTransferRegistry transfers, IAntiforgery antiforgery, QuasarBrowserSessions sessions) =>
        {
            await antiforgery.ValidateRequestAsync(http);
            transfers.Cancel(transfers.Get(await sessions.BindHttpSessionAsync(http), id, claim: false));
            return Results.NoContent();
        });
        sdk.MapGet("/settings", async (HttpContext http, QuasarOperationService operations) =>
            Results.Json(await operations.GetSettingsAsync(http.User)));
        sdk.MapPost("/settings/{pluginId}/{operationId}", async (string pluginId, string operationId,
            HttpContext http, QuasarOperationService operations, IAntiforgery antiforgery) =>
        {
            // Check authorization before parsing submitted settings, and again inside the atomic save gate.
            await operations.GetSettingsAsync(http.User);
            await antiforgery.ValidateRequestAsync(http);
            using var body = await ReadJsonAsync(http);
            if (body is null) return Results.Problem(statusCode: 413);
            var rule = body.RootElement.ValueKind == JsonValueKind.Null ? null
                : body.RootElement.Deserialize<QuasarOperationRule>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
            await operations.SaveRuleAsync(http.User, pluginId, operationId, rule, http.RequestAborted);
            return Results.NoContent();
        });
    }

    internal static async Task<JsonDocument?> ReadJsonAsync(HttpContext http)
    {
        var bytes = new byte[MaximumJsonBytes + 1];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = await http.Request.Body.ReadAsync(bytes.AsMemory(count), http.RequestAborted);
            if (read == 0) break;
            count += read;
        }
        return count > MaximumJsonBytes ? null
            : JsonDocument.Parse(bytes.AsMemory(0, count), new JsonDocumentOptions { MaxDepth = QuasarSdkLimits.MaximumJsonDepth });
    }
}
