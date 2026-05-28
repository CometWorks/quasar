using Discord;
using Discord.WebSocket;

namespace Quasar.Services.Discord;

public sealed class DiscordLogRelayService
{
    private const int ChunkSize = 1900;
    private readonly object _sync = new();
    private readonly DedicatedServerSupervisor _supervisor;
    private readonly DiscordRateLimiter _rateLimiter;
    private readonly ILogger<DiscordLogRelayService> _logger;
    private readonly Dictionary<string, LogCursorState> _offsets = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Task> _tasks = [];

    public DiscordLogRelayService(
        DedicatedServerSupervisor supervisor,
        DiscordRateLimiter rateLimiter,
        ILogger<DiscordLogRelayService> logger)
    {
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
                         instance.EnableLogExport &&
                         instance.LogChannelId.HasValue))
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
            _offsets.Clear();
            _tasks.Clear();
        }
    }

    private async Task RunLoopAsync(DiscordSocketClient client, DiscordInstanceOptions instanceOptions, CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Max(1, instanceOptions.LogExportIntervalMinutes)));
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
            var snapshot = _supervisor.GetSnapshots()
                .FirstOrDefault(item => string.Equals(item.InstanceId, instanceOptions.InstanceId, StringComparison.OrdinalIgnoreCase));
            if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.StandardOutputLogPath))
                return;

            var delta = await ReadDeltaAsync(instanceOptions.InstanceId, snapshot.StandardOutputLogPath, cancellationToken);
            if (string.IsNullOrWhiteSpace(delta))
                return;

            if (client.GetChannel(instanceOptions.LogChannelId!.Value) is not IMessageChannel channel)
                return;

            foreach (var chunk in ChunkText(delta, ChunkSize))
            {
                var codeBlock = $"```\n{EscapeCodeBlock(chunk)}\n```";
                await _rateLimiter.RunAsync(instanceOptions.LogChannelId.Value, () => channel.SendMessageAsync(text: codeBlock), cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Discord log export failed for instance {InstanceId}", instanceOptions.InstanceId);
        }
    }

    private async Task<string> ReadDeltaAsync(string instanceId, string filePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
            return string.Empty;

        long offset;
        lock (_sync)
        {
            if (!_offsets.TryGetValue(instanceId, out var state) ||
                !string.Equals(state.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
            {
                state = new LogCursorState
                {
                    FilePath = filePath,
                    Offset = 0,
                };
                _offsets[instanceId] = state;
            }

            offset = state.Offset;
        }

        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (offset > stream.Length)
            offset = 0;

        stream.Seek(offset, SeekOrigin.Begin);

        using var reader = new StreamReader(stream);
        var contents = await reader.ReadToEndAsync(cancellationToken);
        var newOffset = stream.Position;

        lock (_sync)
        {
            if (_offsets.TryGetValue(instanceId, out var state) &&
                string.Equals(state.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
            {
                state.Offset = newOffset;
            }
        }

        return contents;
    }

    private static IEnumerable<string> ChunkText(string value, int chunkSize)
    {
        if (string.IsNullOrEmpty(value))
            yield break;

        for (var index = 0; index < value.Length; index += chunkSize)
            yield return value.Substring(index, Math.Min(chunkSize, value.Length - index));
    }

    private static string EscapeCodeBlock(string value)
    {
        return value.Replace("```", "``\u200B`", StringComparison.Ordinal);
    }

    private sealed class LogCursorState
    {
        public string FilePath { get; set; } = string.Empty;

        public long Offset { get; set; }
    }
}
