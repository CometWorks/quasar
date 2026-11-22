using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Services.Auth;
using Quasar.Services.Discord;
using static Quasar.Services.Plugins.QuasarCoreOperations;

namespace Quasar.Services.Plugins;

internal static class QuasarDiscordDashboardOperations
{
    internal sealed record LogInput(bool Exceptions = false);
    private static readonly QuasarOperationMetadata Read = new() { Effect = QuasarOperationEffect.Read, RequireDefaultPolicies = true, AgentAccess = QuasarAgentAccess.Denied };
    private static readonly QuasarOperationMetadata Change = new() { RequireDefaultPolicies = true, AgentAccess = QuasarAgentAccess.Denied };

    internal static void AddDiscordDashboardOperations(this IServiceCollection services)
    {
        string[] manage = [QuasarPolicyNames.CanView, QuasarPolicyNames.CanManageDiscord];
        services.AddQuasarOperation<EmptyRequest, DiscordDashboardState>(PluginId, "discord.dashboard", [QuasarPolicyNames.CanView], async (caller, _, _) =>
        {
            var scope = caller.Services;
            var editor = (await scope.GetRequiredService<IAuthorizationService>().AuthorizeAsync(caller.User, null, QuasarPolicyNames.CanManageDiscord)).Succeeded;
            var status = scope.GetRequiredService<DiscordBotService>().GetStatus();
            var options = scope.GetRequiredService<DiscordOptionsCatalog>().GetOptions();
            options.BotToken = "";
            if (!editor) status = new() { Enabled = status.Enabled, IsRunning = status.IsRunning, StateText = status.StateText };
            var targets = scope.GetRequiredService<DedicatedServerCatalog>().GetServers().Select(s => new DiscordTarget(s.UniqueName, s.UniqueName, s.DisplayName, false))
                .Concat(scope.GetRequiredService<ClusterCatalog>().GetClusters().Where(c => caller.User.CanQueryCluster(c.UniqueName))
                    .Select(c => new DiscordTarget("cluster:" + c.UniqueName, c.UniqueName, (string.IsNullOrWhiteSpace(c.DisplayName) ? c.UniqueName : c.DisplayName) + " (cluster)", true))).ToArray();
            options.Servers.RemoveAll(s => s.IsCluster && !caller.User.CanQueryCluster(s.UniqueName));
            // Send the settings JSON as text so the JS bridge preserves 64-bit Discord snowflakes.
            return new(editor, targets, JsonSerializer.Serialize(options), editor ? Magnetar.Protocol.Runtime.MagnetarPaths.GetQuasarDeathMessagesPath() : null, editor ? scope.GetRequiredService<DeathMessagesCatalog>().GetConfig() : null, status);
        }, Read with { Description = "Returns Discord integration status and settings." });
        services.AddQuasarOperation<DiscordSettingsSave, bool>(PluginId, "discord.settings.save", manage, async (caller, input, ct) =>
        {
            var options = JsonSerializer.Deserialize<DiscordOptions>(input.Settings) ?? throw new QuasarOperationException(400, "Invalid Discord settings.");
            var store = caller.Services.GetRequiredService<DiscordOptionsCatalog>();
            var saved = store.GetOptions();
            if (options.Servers.Any(s => s.IsCluster && !caller.User.CanQueryCluster(s.UniqueName))) throw new QuasarOperationException(403, "Cluster access denied.");
            options.Servers.AddRange(saved.Servers.Where(s => s.IsCluster && !caller.User.CanQueryCluster(s.UniqueName)));
            options.BotToken = input.ClearToken ? "" : string.IsNullOrWhiteSpace(input.BotToken) ? saved.BotToken : input.BotToken;
            await store.SaveAsync(options, ct);
            return true;
        }, Change with { Description = "Saves Discord integration settings." });
        services.AddQuasarOperation<DiscordPresenceOptions, DiscordPresencePreview>(PluginId, "discord.presence.preview", manage, (caller, input, _) =>
        {
            var preview = caller.Services.GetRequiredService<DiscordBotService>().PreviewPresence(input);
            return Task.FromResult(new DiscordPresencePreview(preview.Status.ToString(), preview.Activity, preview.ActivityType.ToString()));
        }, Read with { Description = "Previews the configured Discord presence." });
        services.AddQuasarOperation<DeathMessagesConfig, bool>(PluginId, "discord.templates.save", manage, async (caller, input, ct) =>
        {
            await caller.Services.GetRequiredService<DeathMessagesCatalog>().SaveAsync(input, ct);
            return true;
        }, Change with { Description = "Saves the reviewed Discord message templates." });
        services.AddQuasarOperation<EmptyRequest, DeathMessagesConfig>(PluginId, "discord.templates.reset", manage, async (caller, _, ct) =>
        {
            var store = caller.Services.GetRequiredService<DeathMessagesCatalog>();
            await store.ResetAsync(ct);
            return store.GetConfig();
        }, Change with { Description = "Restores default Discord message templates." });
        services.AddQuasarRead<LogInput>(PluginId, "discord.log.excerpt", manage, async (caller, input, ct) =>
        {
            var path = QuasarLoggingConfigurator.ResolveDiscordLogPath(caller.Services.GetRequiredService<WebServiceOptions>());
            var excerpt = new DiscordLogExcerpt(path, true, false, "");
            if (File.Exists(path))
            {
                var (text, truncated) = await DiscordLogExcerptReader.ReadTailAsync(path, input.Exceptions ? 4 * 1024 * 1024 : 256 * 1024);
                excerpt = new(path, false, truncated, input.Exceptions ? DiscordLogExcerptReader.ExtractExceptionBlocks(text, truncated, "Discord log") : text);
            }
            var bytes = JsonSerializer.SerializeToUtf8Bytes(excerpt, JsonSerializerOptions.Web);
            return new(_ => Task.FromResult<Stream>(new MemoryStream(bytes, false)), "application/json", Length: bytes.Length);
        }, Read with { Description = "Streams a bounded excerpt of the Discord integration log." });
    }
}
