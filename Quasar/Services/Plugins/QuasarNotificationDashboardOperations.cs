using Microsoft.AspNetCore.Authorization;
using Quasar.Client.Administration;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Services.Auth;
using Quasar.Services.Updates;

namespace Quasar.Services.Plugins;

internal static class QuasarNotificationDashboardOperations
{
    public sealed record PushEndpoint(string EndpointSecret);
    public sealed record PushInput(BrowserPushSubscription SubscriptionSecret);

    internal static void AddQuasarNotificationDashboard(this IServiceCollection services)
    {
        const string plugin = QuasarCoreOperations.PluginId;
        string[] view = [QuasarPolicyNames.CanView];
        var write = new QuasarOperationMetadata { RequireDefaultPolicies = true, AgentAccess = QuasarAgentAccess.Denied };
        var read = write with { Effect = QuasarOperationEffect.Read };
        services.AddQuasarOperation<QuasarCoreOperations.EmptyRequest, DashboardNotices>(plugin, "notifications.current", view,
            async (caller, _, token) =>
            {
                var delivery = caller.Services.GetRequiredService<PushNotificationService>();
                var notices = (await delivery.GetNoticesAsync(caller.User, token)).Where(delivery.IsCurrent).ToArray();
                return new(notices, caller.Services.GetRequiredService<QuasarUpdateService>().GetSnapshot().GitHubTokenExpired);
            }, read);
        services.AddQuasarOperation<QuasarCoreOperations.EmptyRequest, NotificationPreferences>(plugin, "notifications.preferences", view,
            (caller, _, token) => caller.Services.GetRequiredService<PushNotificationService>().GetPreferencesAsync(caller.User, token), read);
        services.AddQuasarOperation<NotificationPreferences, bool>(plugin, "notifications.preferences.save", view,
            async (caller, preferences, token) =>
            {
                var delivery = caller.Services.GetRequiredService<PushNotificationService>();
                if (!(await caller.Services.GetRequiredService<IAuthorizationService>().AuthorizeAsync(caller.User, QuasarPolicyNames.CanManageSecurity)).Succeeded)
                    preferences.UiPluginUpdates = (await delivery.GetPreferencesAsync(caller.User, token)).UiPluginUpdates;
                try { await delivery.SavePreferencesAsync(caller.User, preferences, token); }
                catch (InvalidDataException error) { throw new QuasarOperationException(400, error.Message); }
                return true;
            }, write);
        services.AddQuasarOperation<QuasarCoreOperations.EmptyRequest, string>(plugin, "notifications.push.key", view,
            (caller, _, token) => caller.Services.GetRequiredService<PushNotificationService>().GetPublicKeyAsync(token), read);
        services.AddQuasarOperation<PushEndpoint, bool>(plugin, "notifications.push.status", view,
            (caller, input, token) => caller.Services.GetRequiredService<PushNotificationService>().IsRegisteredAsync(caller.User, input.EndpointSecret, token), read);
        services.AddQuasarOperation<PushInput, bool>(plugin, "notifications.push.subscribe", view,
            async (caller, input, token) =>
            {
                if (input.SubscriptionSecret is null) throw new QuasarOperationException(400, "Missing push subscription.");
                await caller.Services.GetRequiredService<PushNotificationService>().SubscribeAsync(caller.User, input.SubscriptionSecret, token);
                return true;
            }, write);
        services.AddQuasarOperation<PushEndpoint, bool>(plugin, "notifications.push.unsubscribe", view,
            async (caller, input, token) =>
            {
                await caller.Services.GetRequiredService<PushNotificationService>().UnsubscribeAsync(caller.User, input.EndpointSecret, token);
                return true;
            }, write);
    }
}
