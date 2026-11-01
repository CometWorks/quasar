namespace CometWorks.ClusterGateway.AdminContract.V1;

/// <summary>A Manager-assigned due time for exactly one running node incarnation.</summary>
public sealed record RotationAssignment(string Node, long Epoch, DateTimeOffset DueAt);

/// <summary>Replaces the fleet publication. Renew before ValidUntil; never initiates a rotation.</summary>
public sealed record RotationPublication(long ExpectedRevision, string Source, DateTimeOffset ValidUntil,
    bool Enabled, RotationAssignment[] Nodes);

public sealed record RotationStatus(long Revision, string? Source, DateTimeOffset? ValidUntil,
    bool Enabled, RotationAssignment[] Nodes);
