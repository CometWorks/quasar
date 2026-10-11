namespace Quasar.Host.Contract.V1;

public sealed record HostDeploymentReferences(string[] Installations, string[] Mods, string[] Worlds, string[] Configurations);
