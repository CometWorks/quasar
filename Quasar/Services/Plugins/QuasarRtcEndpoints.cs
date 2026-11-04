using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Plugin.Abstractions.Security;

namespace Quasar.Services.Plugins;

public static class QuasarRtcEndpoints
{
    public static IServiceCollection AddQuasarRtc(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<QuasarRtcOptions>(configuration.GetSection("Quasar:WebRtc"));
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<QuasarPeerProcess>();
        services.AddSingleton<QuasarRtcSessions>();
        return services;
    }

    public static void MapQuasarRtc(this WebApplication app)
    {
        var rtc = app.MapGroup(QuasarSdkEndpoints.Prefix + "/rtc").RequireAuthorization(QuasarPolicyNames.CanView);
        rtc.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "private, no-store";
            try { return await next(context); }
            catch (QuasarOperationException error) { return Results.Problem(statusCode: error.StatusCode, detail: error.Message); }
            catch (AntiforgeryValidationException) { return Results.Problem(statusCode: 400, detail: "Invalid antiforgery token."); }
            catch (JsonException) { return Results.Problem(statusCode: 400, detail: "Invalid direct connection request."); }
            catch (Exception error) when (error is IOException or HttpRequestException or TimeoutException)
            { return Results.Problem(statusCode: 503, detail: "Direct connectivity is unavailable."); }
        });
        rtc.MapPost("/attempts", async (HttpContext http, IAntiforgery antiforgery, QuasarBrowserSessions sessions, QuasarRtcSessions peers) =>
        {
            await antiforgery.ValidateRequestAsync(http);
            using var body = await QuasarSdkEndpoints.ReadJsonAsync(http) ?? throw new QuasarOperationException(413, "Direct connection requests are limited to 64 KiB.");
            var request = body.RootElement.Deserialize<QuasarRtcOffer>(new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new QuasarOperationException(400, "A peer offer is required.");
            var binding = await sessions.BindHttpSessionAsync(http);
            return Results.Json(await peers.CreateAsync(binding, HostedPanelRequest.IsHosted(http), request, http.RequestAborted));
        });
        rtc.MapPost("/attempts/{id}/confirm", async (string id, HttpContext http, IAntiforgery antiforgery, QuasarBrowserSessions sessions, QuasarRtcSessions peers) =>
        {
            await antiforgery.ValidateRequestAsync(http);
            using var body = await QuasarSdkEndpoints.ReadJsonAsync(http) ?? throw new QuasarOperationException(413, "Direct connection requests are limited to 64 KiB.");
            if (body.RootElement.ValueKind != JsonValueKind.Object || !body.RootElement.TryGetProperty("grant", out var grant)
                || grant.ValueKind != JsonValueKind.String || grant.GetString() is not { Length: 64 } value)
                throw new QuasarOperationException(400, "A peer setup grant is required.");
            await peers.ConfirmAsync(await sessions.BindHttpSessionAsync(http), id, value, http.RequestAborted);
            return Results.NoContent();
        });
        rtc.MapGet("/attempts/{id}", async (string id, HttpContext http, QuasarBrowserSessions sessions, QuasarRtcSessions peers) =>
        {
            var peer = peers.Get(await sessions.BindHttpSessionAsync(http), id, http.RequestAborted);
            return Results.Json(new { id = peer.Id, version = 1, active = peer.Active, peer.Status.State, peer.Status.Path });
        });
        rtc.MapDelete("/attempts/{id}", async (string id, HttpContext http, IAntiforgery antiforgery, QuasarBrowserSessions sessions, QuasarRtcSessions peers) =>
        {
            await antiforgery.ValidateRequestAsync(http);
            await peers.CloseAsync(await sessions.BindHttpSessionAsync(http), id, http.RequestAborted);
            return Results.NoContent();
        });
    }
}
