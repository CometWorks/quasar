using Discord;
using Discord.WebSocket;
using Quasar.Services.Analytics;

namespace Quasar.Services.Discord;

public sealed class DiscordSimSpeedAlertService
{
    private static readonly TimeSpan FreshSampleAge = TimeSpan.FromSeconds(30);

    private readonly object _sync = new();
    private readonly AgentRegistry _registry;
    private readonly MetricsStoreService _metricsStore;
    private readonly DiscordRateLimiter _rateLimiter;
    private readonly ILogger<DiscordSimSpeedAlertService> _logger;
    private readonly Dictionary<string, SimSpeedRuleState> _states = new(StringComparer.OrdinalIgnoreCase);

    public DiscordSimSpeedAlertService(
        AgentRegistry registry,
        MetricsStoreService metricsStore,
        DiscordRateLimiter rateLimiter,
        ILogger<DiscordSimSpeedAlertService> logger)
    {
        _registry = registry;
        _metricsStore = metricsStore;
        _rateLimiter = rateLimiter;
        _logger = logger;
    }

    public async Task HandleChangedAsync(DiscordSocketClient client, DiscordOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);

        var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var agents = _registry.GetAgents();
        foreach (var serverOptions in options.Servers.Where(server =>
                     server.EnableSimSpeedAlerts &&
                     (server.EnableSimSpeedSharpDropAlerts || server.EnableSimSpeedSustainedAlerts) &&
                     (server.SimSpeedAlertChannelId.HasValue || server.AnalyticsChannelId.HasValue)))
        {
            if (!agents.Any(agent =>
                    agent.IsConnected &&
                    agent.Snapshot is { IsRunning: true } &&
                    string.Equals(agent.UniqueNameKey, serverOptions.UniqueName, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var channelId = serverOptions.SimSpeedAlertChannelId ?? serverOptions.AnalyticsChannelId;
            if (!channelId.HasValue)
                continue;

            var store = _metricsStore.GetStore(serverOptions.UniqueName);
            if (store is null)
                continue;

            var samples = store.Raw.ReadLatest(Math.Max(2, Math.Min(3605, serverOptions.SimSpeedSustainedSeconds + 5)));
            if (samples.Length < 2)
                continue;

            var latest = samples[^1];
            if (nowUnix - latest.TimestampUnixSeconds > FreshSampleAge.TotalSeconds)
                continue;

            var alerts = Evaluate(serverOptions, samples);
            if (alerts.Count == 0)
                continue;

            if (client.GetChannel(channelId.Value) is not IMessageChannel channel)
                continue;

            foreach (var alert in alerts)
            {
                try
                {
                    await _rateLimiter.RunAsync(channelId.Value, () => channel.SendMessageAsync(embed: alert.Build()), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(exception, "Failed sending simspeed alert for server {UniqueName}", serverOptions.UniqueName);
                }
            }
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            _states.Clear();
        }
    }

    private IReadOnlyList<EmbedBuilder> Evaluate(DiscordServerOptions options, MetricSample[] samples)
    {
        var latest = samples[^1];
        var alerts = new List<EmbedBuilder>();

        lock (_sync)
        {
            if (!_states.TryGetValue(options.UniqueName, out var state))
            {
                state = new SimSpeedRuleState();
                _states[options.UniqueName] = state;
            }

            if (latest.TimestampUnixSeconds <= state.LastEvaluatedUnix)
                return alerts;

            var evaluateAfterUnix = state.LastEvaluatedUnix > 0
                ? state.LastEvaluatedUnix
                : latest.TimestampUnixSeconds - (long)FreshSampleAge.TotalSeconds;

            if (options.EnableSimSpeedSharpDropAlerts)
                TryAddSharpDropAlert(options, samples, evaluateAfterUnix, state, alerts);

            if (options.EnableSimSpeedSustainedAlerts)
                TryAddSustainedAlert(options, samples, latest, state, alerts);

            state.LastEvaluatedUnix = latest.TimestampUnixSeconds;
        }

        return alerts;
    }

    private static void TryAddSharpDropAlert(
        DiscordServerOptions options,
        MetricSample[] samples,
        long evaluateAfterUnix,
        SimSpeedRuleState state,
        List<EmbedBuilder> alerts)
    {
        for (var index = 1; index < samples.Length; index++)
        {
            var previous = samples[index - 1];
            var current = samples[index];
            if (current.TimestampUnixSeconds <= evaluateAfterUnix)
                continue;

            if (!CooldownElapsed(current.TimestampUnixSeconds, state.LastSharpAlertUnix, options.SimSpeedSharpDropCooldownSeconds))
                continue;

            var drop = previous.SimSpeed - current.SimSpeed;
            if (previous.SimSpeed < options.SimSpeedSharpDropPreviousMinimum ||
                current.SimSpeed > options.SimSpeedSharpDropThreshold ||
                drop < options.SimSpeedSharpDropDelta)
            {
                continue;
            }

            state.LastSharpAlertUnix = current.TimestampUnixSeconds;
            alerts.Add(BuildEmbed(
                options.UniqueName,
                "Sharp SimSpeed Drop",
                Color.Red,
                current.TimestampUnixSeconds,
                ("Previous", previous.SimSpeed.ToString("0.000")),
                ("Current", current.SimSpeed.ToString("0.000")),
                ("Drop", drop.ToString("0.000")),
                ("Rule", $"prev >= {options.SimSpeedSharpDropPreviousMinimum:0.000}, current <= {options.SimSpeedSharpDropThreshold:0.000}")));
            return;
        }
    }

    private static void TryAddSustainedAlert(
        DiscordServerOptions options,
        MetricSample[] samples,
        MetricSample latest,
        SimSpeedRuleState state,
        List<EmbedBuilder> alerts)
    {
        if (!CooldownElapsed(latest.TimestampUnixSeconds, state.LastSustainedAlertUnix, options.SimSpeedSustainedCooldownSeconds))
            return;

        var cutoff = latest.TimestampUnixSeconds - options.SimSpeedSustainedSeconds;
        var window = samples.Where(sample => sample.TimestampUnixSeconds >= cutoff).ToArray();
        if (window.Length < 2)
            return;

        var coveredSeconds = latest.TimestampUnixSeconds - window[0].TimestampUnixSeconds;
        if (coveredSeconds < Math.Max(5, options.SimSpeedSustainedSeconds - 2))
            return;

        var average = window.Average(sample => sample.SimSpeed);
        if (average > options.SimSpeedSustainedThreshold)
            return;

        state.LastSustainedAlertUnix = latest.TimestampUnixSeconds;
        alerts.Add(BuildEmbed(
            options.UniqueName,
            "Sustained SimSpeed Loss",
            Color.Orange,
            latest.TimestampUnixSeconds,
            ("Window", $"{coveredSeconds}s"),
            ("Average", average.ToString("0.000")),
            ("Current", latest.SimSpeed.ToString("0.000")),
            ("Rule", $"avg <= {options.SimSpeedSustainedThreshold:0.000} for {options.SimSpeedSustainedSeconds}s")));
    }

    private static bool CooldownElapsed(long latestUnix, long lastAlertUnix, int cooldownSeconds)
    {
        return lastAlertUnix <= 0 || latestUnix - lastAlertUnix >= Math.Max(0, cooldownSeconds);
    }

    private static EmbedBuilder BuildEmbed(
        string uniqueName,
        string title,
        Color color,
        long timestampUnix,
        params (string Name, string Value)[] fields)
    {
        var builder = new EmbedBuilder()
            .WithTitle($"{uniqueName}: {title}")
            .WithColor(color)
            .WithTimestamp(DateTimeOffset.FromUnixTimeSeconds(timestampUnix));

        foreach (var field in fields)
            builder.AddField(field.Name, field.Value, inline: true);

        return builder;
    }

    private sealed class SimSpeedRuleState
    {
        public long LastEvaluatedUnix { get; set; }

        public long LastSharpAlertUnix { get; set; }

        public long LastSustainedAlertUnix { get; set; }
    }
}
