using System.Text;
using System.Text.Json;
using Quasar.Models;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Services.Auth;
using static Quasar.Services.Plugins.QuasarCoreOperations;

namespace Quasar.Services.Plugins;

/// <summary>Server lifecycle, status and log reads of standalone servers.</summary>
public static class QuasarServerOperations
{
    public sealed record ServerListItem(
        string UniqueName,
        string DisplayName,
        string Kind,
        string Goal,
        string? State,
        string? Health,
        int? PlayersOnline,
        int? UptimeSeconds,
        float? SimSpeed,
        DateTimeOffset? LastSaveUtc,
        string ConfigProfileId
    );

    public sealed record ServerStatus(
        string UniqueName,
        string DisplayName,
        string Goal,
        string State,
        string Health,
        string HealthSummary,
        string LastMessage,
        DateTimeOffset? StartedAtUtc,
        DateTimeOffset? StoppedAtUtc,
        int? LastExitCode,
        int RestartAttempts,
        string LatestErrorLogLine,
        bool AgentConnected,
        int PlayersOnline,
        object? Metrics
    );

    public sealed record ServerActionResult(string UniqueName, string Message);

    /// <param name="Lines">1 to 2000.</param>
    /// <param name="From"><c>tail</c> or <c>head</c>.</param>
    /// <param name="Filter">Keeps lines containing this text, ignoring case.</param>
    /// <param name="MaxBytes">1 to 65536.</param>
    public sealed record LogTailRequest(
        string UniqueName = "",
        string? Name = null,
        int Lines = 200,
        string From = "tail",
        string? Filter = null,
        int MaxBytes = 16384
    );

    /// <param name="Truncated">More matching lines exist than were returned, or a line was cut.</param>
    /// <param name="TotalLines">The number of matching lines in the file, when the whole file was read.</param>
    public sealed record LogTailResponse(
        string File,
        IReadOnlyList<string> Lines,
        bool Truncated,
        int? TotalLines
    );

    private static readonly TimeSpan LifecycleDeadline = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan LongDeadline = TimeSpan.FromSeconds(300);
    private static readonly QuasarOperationMetadata Read = new()
    {
        Effect = QuasarOperationEffect.Read,
    };
    private static readonly IQuasarUndoHandler GoalUndo = new GoalStateUndo();

    public static void AddQuasarServerOperations(this IServiceCollection services)
    {
        const string plugin = QuasarCoreOperations.PluginId;
        services.AddQuasarOperation<EmptyRequest, IReadOnlyList<ServerListItem>>(
            plugin,
            "servers.list",
            [QuasarPolicyNames.CanView],
            (caller, _, _) => Task.FromResult(List(caller.Services)),
            Read with
            {
                Description =
                    "Lists the standalone servers and clusters with their goal, state, health, players online, uptime, sim speed, last save and config profile.",
            }
        );
        services.AddQuasarOperation<ServerRequest, ServerStatus>(
            plugin,
            "servers.status",
            [QuasarPolicyNames.CanView],
            (caller, input, _) => Task.FromResult(Status(caller.Services, input.UniqueName)),
            Read with
            {
                Description =
                    "Returns one standalone server's runtime state, health, last message and error, and the live metrics its agent reports.",
            }
        );
        services.AddQuasarOperation<ServerRequest, ServerActionResult>(
            plugin,
            "servers.start",
            [QuasarPolicyNames.CanControlServers],
            StartAsync,
            new()
            {
                Reversal = QuasarReversal.Undo,
                Undo = GoalUndo,
                Deadline = LifecycleDeadline,
                Description =
                    "Starts a standalone server and keeps it on. Returns once the process is launched; the world loads after that.",
            }
        );
        services.AddQuasarOperation<ServerRequest, ServerActionResult>(
            plugin,
            "servers.stop",
            [QuasarPolicyNames.CanControlServers],
            StopAsync,
            new()
            {
                Reversal = QuasarReversal.Undo,
                Undo = GoalUndo,
                Deadline = LifecycleDeadline,
                Description =
                    "Stops a standalone server: it saves and shuts down normally and stays off.",
            }
        );
        services.AddQuasarOperation<ServerRequest, ServerActionResult>(
            plugin,
            "servers.restart",
            [QuasarPolicyNames.CanControlServers],
            async (caller, input, token) =>
            {
                var server = Track(caller, input.UniqueName);
                // The deadline ends the wait, never the restart itself.
                await caller
                    .Services.GetRequiredService<DedicatedServerSupervisor>()
                    .RestartServerAsync(server.UniqueName)
                    .WaitAsync(token);
                return new ServerActionResult(server.UniqueName, "Restart requested.");
            },
            new()
            {
                Reversal = QuasarReversal.NothingToReverse,
                Deadline = LongDeadline,
                Description =
                    "Restarts a standalone server: a normal stop with a world save, then a start.",
            }
        );
        services.AddQuasarOperation<ServerRequest, ServerActionResult>(
            plugin,
            "servers.save",
            [QuasarPolicyNames.CanControlServers],
            async (caller, input, token) =>
            {
                var uniqueName = input.UniqueName.Trim();
                caller.Undo!.Server = uniqueName;
                await caller
                    .Services.GetRequiredService<DedicatedServerSupervisor>()
                    .SaveWorldAsync(uniqueName, token);
                return new ServerActionResult(uniqueName, "World saved.");
            },
            new()
            {
                Reversal = QuasarReversal.NothingToReverse,
                Deadline = LongDeadline,
                Description =
                    "Saves the world of a running standalone server and waits until the save is written.",
            }
        );
        AddLogTail(
            services,
            "logs.server.tail",
            "Dedicated Server",
            QuasarCoreOperations.ResolveDedicatedServerLogPath
        );
        AddLogTail(
            services,
            "logs.magnetar.tail",
            "Magnetar",
            QuasarCoreOperations.ResolveMagnetarInfoLogPath
        );
    }

    private static void AddLogTail(
        IServiceCollection services,
        string operationId,
        string kind,
        Func<string, DedicatedServerCatalog, string?, string?> resolve
    ) =>
        services.AddQuasarOperation<LogTailRequest, LogTailResponse>(
            QuasarCoreOperations.PluginId,
            operationId,
            [QuasarPolicyNames.CanView],
            async (caller, input, token) =>
            {
                var path = resolve(
                    input.UniqueName,
                    caller.Services.GetRequiredService<DedicatedServerCatalog>(),
                    input.Name
                );
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    throw caller
                        .Services.GetRequiredService<DedicatedServerCatalog>()
                        .GetServer(input.UniqueName) is null
                        ? NotFound(
                            caller.Services,
                            input.UniqueName,
                            "use cluster.logs.tail for its node plugin logs and cluster.events.tail for its Cluster Registry events"
                        )
                        : new QuasarOperationException(404, "Log file not found.");
                return await ReadLogSliceAsync(path, input, token);
            },
            Read with
            {
                Description =
                    $"Reads lines of a server's {kind} log, the newest file unless a name is given: the last (tail) or first (head) lines, optionally only those containing a filter text. Defaults: 200 lines, at most 16 KiB.",
            }
        );

    internal static async Task<LogTailResponse> ReadLogSliceAsync(
        string path,
        LogTailRequest request,
        CancellationToken token
    )
    {
        var slice = new LogSlice(request);
        await using var file = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            QuasarSdkLimits.TransferBufferBytes,
            FileOptions.Asynchronous
        );
        if (slice.FromTail)
        {
            await foreach (var line in ReadLinesBackwardAsync(file, token))
                if (!slice.Take(line))
                    break;
        }
        else
        {
            using var reader = new StreamReader(file, Encoding.UTF8);
            while (await reader.ReadLineAsync(token) is { } line)
                if (!slice.Take(line))
                    break;
        }
        return slice.Result(Path.GetFileName(path));
    }

    /// <summary>The same slice of lines held in memory, oldest first.</summary>
    internal static LogTailResponse SliceLines(
        string source,
        IReadOnlyList<string> lines,
        LogTailRequest request
    )
    {
        var slice = new LogSlice(request);
        foreach (var line in slice.FromTail ? lines.Reverse() : lines)
            if (!slice.Take(line))
                break;
        return slice.Result(source);
    }

    // Collects lines in read order: newest first from the tail, oldest first from the head.
    private sealed class LogSlice
    {
        private readonly int _wanted;
        private readonly string? _filter;
        private readonly List<string> _lines = [];
        private int _budget;
        private bool _truncated;
        private bool _complete = true;
        private int _matched;

        public LogSlice(LogTailRequest request)
        {
            FromTail = request.From?.Trim().ToLowerInvariant() switch
            {
                null or "" or "tail" => true,
                "head" => false,
                _ => throw new QuasarOperationException(400, "From must be tail or head."),
            };
            _wanted = Math.Clamp(request.Lines, 1, 2000);
            _budget = Math.Clamp(request.MaxBytes, 1, 65536);
            _filter = string.IsNullOrEmpty(request.Filter) ? null : request.Filter;
        }

        public bool FromTail { get; }

        // Returns false once the slice is full; the rest is then only counted as "more".
        public bool Take(string line)
        {
            line = line.TrimEnd('\r');
            if (_filter is not null && !line.Contains(_filter, StringComparison.OrdinalIgnoreCase))
                return true;
            _matched++;
            if (_lines.Count == _wanted || _budget == 0)
            {
                _truncated = true;
                _complete = false;
                return false;
            }
            var bytes = Encoding.UTF8.GetByteCount(line) + 1;
            if (bytes > _budget)
            {
                line = Cut(line, _budget - 1);
                _truncated = true;
                bytes = _budget;
            }
            _lines.Add(line);
            _budget -= bytes;
            return true;
        }

        public LogTailResponse Result(string source)
        {
            if (FromTail)
                _lines.Reverse();
            return new(source, _lines, _truncated, _complete ? _matched : null);
        }
    }

    // Splits on '\n' bytes from the end of the file, which is safe in UTF-8, a chunk at a time.
    private static async IAsyncEnumerable<string> ReadLinesBackwardAsync(
        FileStream file,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token
    )
    {
        var position = file.Length;
        var carry = Array.Empty<byte>();
        var buffer = new byte[QuasarSdkLimits.TransferBufferBytes];
        var skipFinalNewline = true;
        while (position > 0)
        {
            var count = (int)Math.Min(buffer.Length, position);
            position -= count;
            file.Position = position;
            await file.ReadExactlyAsync(buffer.AsMemory(0, count), token);
            var block = new byte[count + carry.Length];
            Buffer.BlockCopy(buffer, 0, block, 0, count);
            Buffer.BlockCopy(carry, 0, block, count, carry.Length);
            var end = block.Length;
            for (var index = block.Length - 1; index >= 0; index--)
            {
                if (block[index] != (byte)'\n')
                    continue;
                if (!(skipFinalNewline && index == block.Length - 1))
                    yield return Encoding.UTF8.GetString(block, index + 1, end - index - 1);
                skipFinalNewline = false;
                end = index;
            }
            skipFinalNewline = false;
            carry = block[..end];
        }
        if (carry.Length > 0)
            yield return Encoding.UTF8.GetString(carry);
    }

    private static string Cut(string line, int maxBytes) =>
        Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(line), 0, maxBytes).TrimEnd('\uFFFD');

    private static IReadOnlyList<ServerListItem> List(IServiceProvider services)
    {
        var runtimes = services
            .GetRequiredService<DedicatedServerSupervisor>()
            .GetSnapshots()
            .ToDictionary(runtime => runtime.UniqueName, StringComparer.OrdinalIgnoreCase);
        var agents = StandaloneAgents(services);
        var servers = services
            .GetRequiredService<DedicatedServerCatalog>()
            .GetServers()
            .Select(server =>
            {
                var runtime = runtimes.GetValueOrDefault(server.UniqueName);
                var metrics = agents.GetValueOrDefault(server.UniqueName)?.Snapshot?.Metrics;
                return new ServerListItem(
                    server.UniqueName,
                    server.DisplayName,
                    "standalone",
                    server.GoalState.ToString(),
                    (runtime?.State ?? DedicatedServerProcessState.Stopped).ToString(),
                    runtime?.HealthState.ToString(),
                    metrics?.PlayersOnline,
                    metrics?.UptimeSeconds,
                    metrics?.SimSpeed,
                    metrics?.LastWorldSaveUtc,
                    server.ConfigProfileId
                );
            });
        var clusters = (services.GetService<ClusterCatalog>()?.GetClusters() ?? []).Select(
            cluster => new ServerListItem(
                cluster.UniqueName,
                cluster.DisplayName,
                "cluster",
                cluster.GoalState.ToString(),
                null,
                null,
                null,
                null,
                null,
                null,
                cluster.ConfigProfileId
            )
        );
        return [.. servers, .. clusters];
    }

    private static ServerStatus Status(IServiceProvider services, string uniqueName)
    {
        var server = QuasarCoreOperations.RequireServer(services, uniqueName);
        var runtime = services
            .GetRequiredService<DedicatedServerSupervisor>()
            .GetSnapshots()
            .FirstOrDefault(snapshot =>
                snapshot.UniqueName.Equals(server.UniqueName, StringComparison.OrdinalIgnoreCase)
            );
        var agent = StandaloneAgents(services).GetValueOrDefault(server.UniqueName);
        return new(
            server.UniqueName,
            server.DisplayName,
            server.GoalState.ToString(),
            (runtime?.State ?? DedicatedServerProcessState.Stopped).ToString(),
            (runtime?.HealthState ?? DedicatedServerHealthState.Unknown).ToString(),
            runtime?.HealthSummary ?? string.Empty,
            runtime?.LastMessage ?? string.Empty,
            runtime?.StartedAtUtc,
            runtime?.StoppedAtUtc,
            runtime?.LastExitCode,
            runtime?.RestartAttempts ?? 0,
            runtime?.LatestErrorLogLine ?? string.Empty,
            agent is not null,
            agent?.Snapshot?.Players.Count ?? 0,
            agent?.Snapshot?.Metrics
        );
    }

    private static Dictionary<string, AgentRuntimeState> StandaloneAgents(
        IServiceProvider services
    ) =>
        services
            .GetRequiredService<AgentRegistry>()
            .GetAgents()
            .Where(agent => agent.IsConnected && !agent.IsCluster)
            .GroupBy(agent => agent.UniqueNameKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.OrdinalIgnoreCase
            );

    private static DedicatedServerDefinition Track(QuasarOperationContext caller, string uniqueName)
    {
        var server = QuasarCoreOperations.RequireServer(caller.Services, uniqueName);
        caller.Undo!.Server = server.UniqueName;
        return server;
    }

    private static async Task<ServerActionResult> StartAsync(
        QuasarOperationContext caller,
        ServerRequest input,
        CancellationToken token
    )
    {
        var server = Track(caller, input.UniqueName);
        if (caller.Services.GetService<ManagedRuntimeWarmupService>() is { IsReady: false } warmup)
            throw new QuasarOperationException(409, warmup.GetSnapshot().Message);
        if (WorldLaunchBlock(server) is { } blocked)
            throw new QuasarOperationException(409, blocked);
        if (server.GoalState == DedicatedServerGoalState.Off)
            caller.Undo!.SetData(new GoalChange(server.UniqueName, DedicatedServerGoalState.On));
        await StartCoreAsync(caller.Services, server.UniqueName, token);
        return new(server.UniqueName, "Start requested.");
    }

    private static async Task<ServerActionResult> StopAsync(
        QuasarOperationContext caller,
        ServerRequest input,
        CancellationToken token
    )
    {
        var server = Track(caller, input.UniqueName);
        if (server.GoalState == DedicatedServerGoalState.On)
            caller.Undo!.SetData(new GoalChange(server.UniqueName, DedicatedServerGoalState.Off));
        await caller
            .Services.GetRequiredService<DedicatedServerSupervisor>()
            .SetGoalStateAsync(server.UniqueName, DedicatedServerGoalState.Off, token);
        return new(server.UniqueName, "Stop requested.");
    }

    // What the Home and Servers pages do: record the goal without a competing reconcile, then start.
    private static async Task StartCoreAsync(
        IServiceProvider services,
        string uniqueName,
        CancellationToken token
    )
    {
        var supervisor = services.GetRequiredService<DedicatedServerSupervisor>();
        await supervisor.SetGoalStateAsync(
            uniqueName,
            DedicatedServerGoalState.On,
            reconcile: false,
            token
        );
        // The deadline ends the wait, never the start itself.
        await supervisor.StartServerAsync(uniqueName).WaitAsync(token);
    }

    internal static string? WorldLaunchBlock(DedicatedServerDefinition server)
    {
        if (string.IsNullOrWhiteSpace(server.WorldSaveName))
            return "Select a world save before starting this server.";
        var worldPath = server.GetWorldSavePath();
        if (string.IsNullOrWhiteSpace(worldPath) || !Directory.Exists(worldPath))
            return $"World save folder not found: {worldPath}";
        return File.Exists(Path.Combine(worldPath, "Sandbox.sbc"))
            ? null
            : $"World save '{worldPath}' is missing Sandbox.sbc.";
    }

    private sealed record GoalChange(string UniqueName, DedicatedServerGoalState After);

    // Undo of a start stops the server again, undo of a stop starts it, while the goal is still what the action left.
    private sealed class GoalStateUndo : IQuasarUndoHandler
    {
        public Task<string?> CheckAsync(QuasarUndoContext context, CancellationToken token)
        {
            var change = context.Data.Deserialize<GoalChange>(QuasarCoreOperations.Json)!;
            var server = context
                .Services.GetRequiredService<DedicatedServerCatalog>()
                .GetServer(change.UniqueName);
            return Task.FromResult(
                server is null ? $"Server '{change.UniqueName}' no longer exists."
                : server.GoalState == change.After ? null
                : $"The server's goal is {server.GoalState} now, not {change.After}."
            );
        }

        public async Task ReverseAsync(QuasarUndoContext context, CancellationToken token)
        {
            var change = context.Data.Deserialize<GoalChange>(QuasarCoreOperations.Json)!;
            if (change.After == DedicatedServerGoalState.On)
                await context
                    .Services.GetRequiredService<DedicatedServerSupervisor>()
                    .SetGoalStateAsync(change.UniqueName, DedicatedServerGoalState.Off, token);
            else
                await StartCoreAsync(context.Services, change.UniqueName, token);
        }
    }
}
