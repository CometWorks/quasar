using System;

namespace CometWorks.ClusterGateway.AdminContract.V1;

public enum RotationState : byte
{
    Unknown, Assigned, PolicyEstimate, Disabled, HotSpare, NotRotated, Draining, NotServing, Stopped, ForceDrainArmed
}

public sealed class NodeDiagnostics
{
    public string Name { get; set; } = "";
    public string Endpoint { get; set; } = "";
    public long Epoch { get; set; }
    public long AgeSeconds { get; set; } = -1;
    public long ProcessUptimeSeconds { get; set; } = -1;
    public long ServingSeconds { get; set; } = -1;
    public long DrainAt { get; set; }
    public long RotationDueAt { get; set; }
    public long RotationExpiresAt { get; set; }
    public RotationState Rotation { get; set; }
    public string State { get; set; } = "Unknown";
    public bool Serving { get; set; }
    public bool Spare { get; set; }
    public bool Authority { get; set; }
    public long AuthorityGeneration { get; set; }
    public long LeaseAt { get; set; }
}

public sealed class DiagnosticsSnapshot
{
    public NodeDiagnostics Current { get; set; } = new NodeDiagnostics();
    public bool Admin { get; set; }
    public bool RegistryAvailable { get; set; }
    public long ServerAt { get; set; }
    public long CapturedAt { get; set; }
    public string Registry { get; set; } = "";
    public int TotalNodes { get; set; }
    public NodeDiagnostics[] Nodes { get; set; } = Array.Empty<NodeDiagnostics>();
}
