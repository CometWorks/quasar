using Discord;
using Discord.WebSocket;
using Quasar.Models;
using Quasar.Services.Analytics;

namespace Quasar.Services.Discord;

public sealed class DiscordAnalyticsExportService
{
    private readonly object _sync = new();
    private readonly MetricsStoreService _metricsStore;
    private readonly DedicatedServerSupervisor _supervisor;
    private readonly DiscordRateLimiter _rateLimiter;
    private readonly ILogger<DiscordAnalyticsExportService> _logger;
    private readonly List<Task> _tasks = [];

    public DiscordAnalyticsExportService(
        MetricsStoreService metricsStore,
        DedicatedServerSupervisor supervisor,
        DiscordRateLimiter rateLimiter,
        ILogger<DiscordAnalyticsExportService> logger)
    {
        _metricsStore = metricsStore;
        _supervisor = supervisor;
        _rateLimiter = rateLimiter;
        _logger = logger;
    }

    public Task StartAsync(DiscordSocketClient client, DiscordOptions options, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            _tasks.Clear();

            foreach (var instanceOptions in options.Instances.Where(instance =>
                         instance.EnableAnalyticsExport &&
                         instance.AnalyticsChannelId.HasValue))
            {
                var cloned = instanceOptions.Clone();
                _tasks.Add(Task.Run(() => RunLoopAsync(client, cloned, cancellationToken), CancellationToken.None));
            }
        }

        return Task.CompletedTask;
    }

    public void Reset()
    {
        lock (_sync)
        {
            _tasks.Clear();
        }
    }

    private async Task RunLoopAsync(DiscordSocketClient client, DiscordInstanceOptions instanceOptions, CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Max(1, instanceOptions.AnalyticsExportIntervalMinutes)));
            while (await timer.WaitForNextTickAsync(cancellationToken))
                await ExportAsync(client, instanceOptions, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ExportAsync(DiscordSocketClient client, DiscordInstanceOptions instanceOptions, CancellationToken cancellationToken)
    {
        try
        {
            var store = _metricsStore.GetStore(instanceOptions.InstanceId);
            if (store is null)
                return;

            var intervalMinutes = Math.Max(1, instanceOptions.AnalyticsExportIntervalMinutes);
            var samples = store.OneMinute.ReadLatest(intervalMinutes);
            if (samples.Length == 0)
                return;

            if (client.GetChannel(instanceOptions.AnalyticsChannelId!.Value) is not IMessageChannel channel)
                return;

            var snapshot = _supervisor.GetSnapshots()
                .FirstOrDefault(item => string.Equals(item.InstanceId, instanceOptions.InstanceId, StringComparison.OrdinalIgnoreCase));
            var latest = samples[^1];
            var embed = new EmbedBuilder()
                .WithTitle($"{snapshot?.Name ?? instanceOptions.InstanceId} analytics")
                .WithColor(Color.DarkBlue)
                .WithTimestamp(DateTimeOffset.FromUnixTimeSeconds(latest.TimestampUnixSeconds))
                .AddField("Window", $"{intervalMinutes} minute(s)", inline: true)
                .AddField("Avg SimSpeed", Average(samples, item => item.SimSpeed).ToString("0.000"), inline: true)
                .AddField("Avg CPU", $"{Average(samples, item => item.CpuPercent):0.0}%", inline: true)
                .AddField("Avg Memory", $"{Average(samples, item => item.MemoryMb):0.0} MB", inline: true)
                .AddField("Max Players", samples.Max(item => item.PlayersOnline).ToString(), inline: true)
                .AddField("Latest PCU", latest.UsedPcu.ToString(), inline: true)
                .AddField("Latest Grids", latest.ActiveGridCount >= 0 ? latest.ActiveGridCount.ToString() : "n/a", inline: true)
                .AddField("Latest Entities", latest.ActiveEntityCount >= 0 ? latest.ActiveEntityCount.ToString() : "n/a", inline: true)
                .AddField("Uptime", FormatUptime(snapshot), inline: true)
                .AddField("State", snapshot?.State.ToString() ?? "Unknown", inline: true);

            await _rateLimiter.RunAsync(instanceOptions.AnalyticsChannelId.Value, () => channel.SendMessageAsync(embed: embed.Build()), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Discord analytics export failed for instance {InstanceId}", instanceOptions.InstanceId);
        }
    }

    private static float Average(IReadOnlyList<MetricSample> samples, Func<MetricSample, float> selector)
    {
        if (samples.Count == 0)
            return 0f;

        var total = 0f;
        foreach (var sample in samples)
            total += selector(sample);

        return total / samples.Count;
    }

    private static string FormatUptime(DedicatedServerInstanceRuntimeSnapshot? snapshot)
    {
        if (snapshot?.StartedAtUtc is null)
            return "n/a";

        var duration = DateTimeOffset.UtcNow - snapshot.StartedAtUtc.Value;
        if (duration.TotalDays >= 1)
            return $"{(int)duration.TotalDays}d {duration.Hours}h {duration.Minutes}m";

        if (duration.TotalHours >= 1)
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";

        if (duration.TotalMinutes >= 1)
            return $"{(int)duration.TotalMinutes}m {duration.Seconds}s";

        return $"{Math.Max(0, duration.Seconds)}s";
    }
}
