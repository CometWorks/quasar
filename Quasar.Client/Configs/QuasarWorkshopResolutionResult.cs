using Quasar.Models;

namespace Quasar.Services;

public sealed record QuasarWorkshopResolutionResult(
    IReadOnlyList<QuasarModSelection> Mods,
    IReadOnlyList<string> Warnings);


public sealed record QuasarModDependencyResolutionResult(
    IReadOnlyList<QuasarModSelection> Mods,
    int AddedDependencyCount,
    int DependencyCount,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<QuasarModDependencyTreeRow> DependencyTreeRows);

public sealed record QuasarModDependencyTreeRow(
    long WorkshopId,
    string DisplayName,
    int Depth,
    bool IsRoot,
    bool IsDependency,
    bool IsAlreadyListed,
    bool IsCycle,
    long? CycleTargetId);

public sealed record QuasarWorkshopAvailabilityResult(
    IReadOnlyList<QuasarModSelection> UnavailableMods,
    int CheckedCount);

public sealed record QuasarWorkshopSearchResultSet(
    IReadOnlyList<QuasarWorkshopSearchResult> Mods,
    int Total);

public sealed record QuasarWorkshopSearchResult(
    long WorkshopId,
    string Title,
    string Description,
    string PreviewUrl,
    IReadOnlyList<string> Tags);
