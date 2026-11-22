using Quasar.Models;
using Quasar.Services.Backup;

namespace Quasar.Client.Administration;

public sealed record BackupServer(string UniqueName, string DisplayName, bool Active, bool Running);
public sealed record BackupJobNotice(Guid Id, bool Success, string Message);
public sealed record BackupDashboard(string Version, QuasarBackupSettings Settings, QuasarBackupDirectorySettings Directory,
    IReadOnlyList<BackupServer> Servers, IReadOnlyList<QuasarBackupFileInfo> Backups, BackupJobNotice[] Jobs);
public sealed record FolderItem(string Name, string Path);
public sealed record BackupFolder(string Path, FolderItem[] Folders, FolderItem[] Breadcrumbs, FolderItem[] Shortcuts);
