namespace Quasar.Plugin.Abstractions.Companion;

public interface IQuasarCompanionChannel
{
    Task<TResponse> SendAsync<TRequest, TResponse>(
        string serverId,
        string companionPluginId,
        string operation,
        TRequest request,
        CancellationToken cancellationToken);
}
