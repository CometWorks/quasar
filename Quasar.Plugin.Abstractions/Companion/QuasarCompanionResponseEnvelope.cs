namespace Quasar.Plugin.Abstractions.Companion;

public sealed class QuasarCompanionResponseEnvelope<TPayload>
{
    public int SchemaVersion { get; init; } = 1;

    public required string CorrelationId { get; init; }

    public bool Success { get; init; }

    public TPayload? Payload { get; init; }

    public string? Error { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];
}
