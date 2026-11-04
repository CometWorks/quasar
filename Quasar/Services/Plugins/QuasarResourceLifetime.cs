using Quasar.Plugin.Abstractions.Operations;

namespace Quasar.Services.Plugins;

public static class QuasarResourceLifetime
{
    public static async Task WatchAuthorizationAsync(Func<CancellationToken, Task> check, CancellationTokenSource stop)
    {
        try
        {
            using var timer = new PeriodicTimer(QuasarSdkLimits.AuthorizationRecheckInterval);
            while (await timer.WaitForNextTickAsync(stop.Token)) await check(stop.Token);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (QuasarOperationException) { stop.Cancel(); }
        catch { stop.Cancel(); throw; }
    }

}
