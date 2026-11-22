using Quasar.Models;

namespace Quasar.Services;

public sealed record FileBrowserEntry(string Name, string FullPath, bool IsWorldFolder);

public sealed record FileBrowserFileEntry(string Name, string FullPath);

public sealed record FileBrowserShortcut(string Label, string FullPath);

public sealed record FileBrowserBreadcrumb(string Label, string FullPath);
