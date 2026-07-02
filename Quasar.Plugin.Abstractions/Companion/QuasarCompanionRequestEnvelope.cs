namespace Quasar.Plugin.Abstractions.Companion;

public sealed class QuasarCompanionRequestEnvelope<TPayload>
{
    public required string PluginId { get; init; }

    public required string Operation { get; init; }

    public int SchemaVersion { get; init; } = 1;

    public required string CorrelationId { get; init; }

    public required TPayload Payload { get; init; }
}
