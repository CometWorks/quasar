namespace Quasar.Client.Fleet;

public sealed record WorldServer(string Id, string Name, string Save, string Profile, string State, string Goal,
    bool Importable, DateTimeOffset Revision);
public sealed record WorldTemplate(string Id, string Name, string Description, DateTimeOffset Updated, bool Exists, long SizeMb);
public sealed record WorldInventory(WorldServer[] Servers, WorldTemplate[] Templates, ConversionDestination[] Profiles, bool EditServers, bool EditConfigs);
public sealed record WorldTarget(Guid Id, string? Server = null, DateTimeOffset? Revision = null);
public sealed record WorldSource(string? Path = null, string? Template = null, bool Stopped = false);
public sealed record WorldCommit(string Name, string Description, string ProfileAction, string? ProfileId = null,
    string? ProfileName = null, string? ProfileDescription = null, bool ProfileOnly = false);
public sealed record WorldMod(string Id, string Name);
public sealed record ReadWorld(string Name, int Settings, WorldMod[] Mods, string? ConfigError);
public sealed record WorldSaved(string? Template, string? Profile, string? Server, string[] Messages);
public sealed record WorldTransferProgress(string Phase, long Bytes, int Completed = 0, int Total = 0);
public sealed record WorldFolderQuery(string? Path = null, bool Hidden = false);
public sealed record WorldFolder(string Name, string Path, bool World = false);
public sealed record WorldFolders(string Path, WorldFolder[] Entries, WorldFolder[] Breadcrumbs, WorldFolder[] Shortcuts, bool World);
public sealed record InstalledWorld(string Name, string Path, string Category, string DisplayPath, string Description);
public sealed record InstalledWorlds(InstalledWorld[] Worlds, string[] Locations, string[] Warnings, bool RuntimeReady);
