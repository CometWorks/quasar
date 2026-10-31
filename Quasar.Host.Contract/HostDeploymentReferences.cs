namespace Quasar.Host.Contract.V1;

public sealed record HostDeploymentReferences(string[] Installations, string[] Mods, string[] Worlds, string[] Configurations);

/// <summary>The preparation workspaces under state/conversions of a deleted cluster whose retention has ended.</summary>
public sealed record HostDeletedClusterData(string[] Workspaces);
