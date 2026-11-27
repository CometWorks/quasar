using Quasar.Hosting;
using Quasar.Plugin.Abstractions.Operations;

namespace Quasar.Services.Plugins;

// Owns the hosted direct-connection port: the setup choice, the external reachability
// check through Nebula's discovery service, and a periodic recheck that keeps the
// advertised public address current after an ISP address change.
public sealed class QuasarDirectPortService(QuasarPeerProcess native, ILogger<QuasarDirectPortService> logger) : BackgroundService
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public event Action? Changed;

    public static HostedDirectPort State => HostedDirectPort.Read() ?? new();

    // A hosted panel answers this setup step once: choose a port or skip it.
    public static bool Pending => HostedPanelState.Read() is { Choice: "hosted", Credential: not null }
        && HostedDirectPort.Read() is not ({ Port: not null } or { Skipped: true });

    public async Task<HostedDirectPort> ConfigureAsync(int? port, bool skip, CancellationToken token)
    {
        if (!skip && (port is not { } value || !HostedDirectPort.IsPort(value)))
            throw new ArgumentException("Use a UDP port from 1024 to 65535.");
        if (HostedPanelState.Read() is not { Choice: "hosted" })
            throw new QuasarOperationException(409, "Direct connections apply to hosted panels. Choose hosted access first.");
        await gate.WaitAsync(token);
        try
        {
            var current = State;
            var next = skip ? new HostedDirectPort { Skipped = true }
                : current.Port == port ? current with { Skipped = false } : new HostedDirectPort { Port = port };
            await next.SaveAsync(token);
            if (next.Port != current.Port || next.Skipped != current.Skipped) await native.RestartAsync();
        }
        finally { gate.Release(); }
        Changed?.Invoke();
        return skip ? State : await ProbeAsync(token);
    }

    public async Task<HostedDirectPort> ProbeAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            var state = State;
            if (HostedPanelState.Read() is not { Choice: "hosted" } || state is not { Port: { }, Skipped: false })
                throw new QuasarOperationException(409, "Choose a direct connection port first.");
            QuasarDirectPortProbe result;
            try { result = await native.ProbeAsync(token); }
            catch (QuasarOperationException error) { result = new(false, null, state.Port.Value, false, error.Message); }
            catch (Exception error) when (error is HttpRequestException or IOException or TaskCanceledException && !token.IsCancellationRequested)
            { result = new(false, null, state.Port.Value, false, "The direct connection component is unavailable."); }
            state = state with
            {
                CheckedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Reachable = result.Reachable,
                // A failed check keeps the last good public address advertised.
                Address = result.Address ?? state.Address, AddressPublic = result.Address is null ? state.AddressPublic : result.Public,
                Error = result.Error is { Length: > 0 and <= 200 } detail ? detail : null,
            };
            await state.SaveAsync(token);
            return state;
        }
        finally
        {
            gate.Release();
            Changed?.Invoke();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
        catch (OperationCanceledException) { return; }
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
        do
        {
            if (State is { Port: not null, Skipped: false } && HostedPanelState.Read() is { Choice: "hosted", Credential: not null })
            {
                try
                {
                    var before = State;
                    var after = await ProbeAsync(stoppingToken);
                    if (before.Reachable && !after.Reachable)
                        logger.LogWarning("Direct connection port {Port}/UDP is no longer reachable from the internet; hosted browsers use the tunnel.", after.Port);
                }
                catch (Exception error) when (!stoppingToken.IsCancellationRequested)
                { logger.LogDebug("Direct port check failed ({Type}).", error.GetType().Name); }
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
