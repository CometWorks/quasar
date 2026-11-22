using Quasar.Services;

namespace Quasar.Client.Fleet;

public sealed record ServerImportTarget(Guid Id, ExistingServerKind Kind);
public sealed record ImportWorld(string Name, string Path, bool IsConfiguredWorld, int SessionSettingCount, int ModCount);
public sealed record ServerImportReview(string ServerName, string ConfigPath, IReadOnlyList<string> Warnings, ImportWorld[] Worlds,
    int RootSettingCount, int SessionSettingCount, int AccessEntryCount, bool HasPasswordHash);
public sealed record ImportedServer(string UniqueName, string DisplayName, string? TemplateId, int TemplateCount, IReadOnlyList<string> Warnings);
public sealed record ImportServerSelection(ExistingServerTransferMode TransferMode, string DisplayName, string UniqueName,
    string[] Worlds, string PrimaryWorld, ExistingServerImportSections Sections, bool Stopped);
