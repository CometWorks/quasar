using Magnetar.Protocol.Model;

namespace Magnetar.Protocol.Transport;

public class AgentWireMessage
{
    public string Kind { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public AgentHello? Hello { get; set; }

    public AgentSnapshot? Snapshot { get; set; }

    public ServerCommandEnvelope? Command { get; set; }

    public ServerCommandResult? CommandResult { get; set; }

    public PluginConfigSnapshot? PluginConfigSnapshot { get; set; }

    public PluginConfigUpdateRequest? PluginConfigUpdateRequest { get; set; }

    public PluginLogBatch? PluginLogs { get; set; }
}
