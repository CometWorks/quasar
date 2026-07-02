namespace Quasar.Services;

public sealed record EntityViewerDialogRequest(
    string AgentId,
    long EntityId,
    string EntityType,
    string EntityDisplayName,
    string ServerName);
