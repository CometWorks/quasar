namespace Quasar.Client.Layout;

public sealed record DashboardPowerState(bool CanRestart, string RestartDetail, int AgentGraceSeconds,
    Guid? UpdateId, string? UpdateVersion, DashboardServerStop? Stop);

public sealed record DashboardServerStop(Guid Id, bool Complete, string Message, bool Failed = false);
