using System.Threading;
using System.Threading.Tasks;
using Magnetar.Protocol.Model;

namespace Magnetar.Protocol.Bridge;

/// <summary>
/// Implemented by Magnetar plugins that accept generic companion requests from
/// Quasar UI plugins through the Quasar.Agent transport.
/// </summary>
public interface IQuasarCompanionRequestHandler
{
    string PluginId { get; }

    Task<CompanionPluginResponse> HandleQuasarCompanionRequestAsync(
        CompanionPluginRequest request,
        CancellationToken cancellationToken);
}
