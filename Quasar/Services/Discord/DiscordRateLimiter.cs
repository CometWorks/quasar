using System.Collections.Concurrent;

namespace Quasar.Services.Discord;

public sealed class DiscordRateLimiter
{
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromMilliseconds(300);
    private readonly ConcurrentDictionary<ulong, ChannelRateState> _states = new();

    public async Task RunAsync(ulong channelId, Func<Task> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        var state = _states.GetOrAdd(channelId, _ => new ChannelRateState());
        await state.Gate.WaitAsync(cancellationToken);

        try
        {
            var now = DateTimeOffset.UtcNow;
            var wait = state.NextAllowedUtc - now;
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, cancellationToken);

            await action();
            state.NextAllowedUtc = DateTimeOffset.UtcNow + MinimumInterval;
        }
        finally
        {
            state.Gate.Release();
        }
    }

    private sealed class ChannelRateState
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public DateTimeOffset NextAllowedUtc { get; set; } = DateTimeOffset.MinValue;
    }
}
