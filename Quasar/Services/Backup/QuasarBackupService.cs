using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Text.Json;
using Magnetar.Protocol.Runtime;
using Quasar.Models;

namespace Quasar.Services.Backup;

/// <summary>An in-memory backup archive ready to stream to the browser.</summary>
public sealed record QuasarBackupArchive(byte[] Content, string FileName);

/// <summary>A backup ZIP found in the Backups directory.</summary>
public sealed record QuasarBackupFileInfo(
    string Name,
    long SizeBytes,
    DateTimeOffset CreatedAtUtc,
    QuasarBackupKind Kind,
    bool Automatic,
    string? ServerUniqueName,
    string? ServerDisplayName);

/// <summary>
/// Builds and restores ZIP backups for Quasar configuration, server runtime
/// state, and world-only data. Every archive contains a <c>quasar-backup.json</c>
/// manifest; configuration archives keep their <c>data/</c> and
/// <c>branding-assets/</c> allow-list, while server/world archives carry the
/// target server metadata plus selected runtime directories.
/// </summary>
public sealed class QuasarBackupService
{
    public const int CurrentFormatVersion = 1;

    private const string ManifestEntryName = "quasar-backup.json";
    private const string DataPrefix = "data/";
    private const string BrandingPrefix = "branding-assets/";
    private const string ServerDefinitionPrefix = "server/";
    private const string DedicatedServerPrefix = "dedicated-server/";
    private const string DedicatedConfigPrefix = "dedicated-config/";
    private const string MagnetarPrefix = "magnetar/";
    private const string WorldPrefix = "world/";
    private const string AutomaticSuffix = "-auto";
    private const string ServerDefinitionEntryName = ServerDefinitionPrefix + "server.json";
    private const string DedicatedServerContentDirectory = "content";
    private const string MagnetarGitHubDirectory = "GitHub";
    private const string MagnetarNuGetDirectory = "NuGet";
    private const string MagnetarPreloaderDirectory = "Preloader";
    private const string MagnetarSourcesDirectory = "Sources";
    private const string MagnetarSourcesPluginsDirectory = "Plugins";
    private const string MagnetarLocalDirectory = "Local";

    private static readonly HashSet<string> ExcludedMagnetarLocalFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Harmony0.dll",
        "Magnetar.Protocol.dll",
        "Quasar.Agent.dll",
    };

    // Singleton config files living directly in the Quasar root (included if present).
    private static readonly string[] SingletonConfigFiles =
    [
        "known-players.json",
        "known-player-settings.json",
        "discord.json",
        "death-messages.json",
        "branding.json",
        "steam-workshop.json",
        "rbac.json",
        "dev-folders.json",
    ];

    // Per-entity definition files. The fixed file names mean History/ (timestamped
    // files) and WorldTemplates World/ snapshots are naturally excluded.
    private static readonly (string Subdirectory, string FileName)[] DefinitionFiles =
    [
        ("Magnetars", "server.json"),
        ("ConfigProfiles", "profile.json"),
        ("WorldTemplates", "template.json"),
    ];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly ILogger<QuasarBackupService> _logger;
    private readonly WebServiceOptions _options;
    private readonly KnownPlayerCatalog _knownPlayers;
    private readonly QuasarDevFolderCatalog _devFolders;
    private readonly DedicatedServerCatalog _servers;
    private readonly DedicatedServerSupervisor _supervisor;
    private readonly ServerRestoreCoordinator _restoreCoordinator;
    private readonly string _brandingAssetsDirectory;

    public event Action? Changed;

    public QuasarBackupService(
        ILogger<QuasarBackupService> logger,
        WebServiceOptions options,
        IWebHostEnvironment environment,
        KnownPlayerCatalog knownPlayers,
        QuasarDevFolderCatalog devFolders,
        DedicatedServerCatalog servers,
        DedicatedServerSupervisor supervisor,
        ServerRestoreCoordinator restoreCoordinator)
    {
        _logger = logger;
        _options = options;
        _knownPlayers = knownPlayers;
        _devFolders = devFolders;
        _servers = servers;
        _supervisor = supervisor;
        _restoreCoordinator = restoreCoordinator;

        var webRootPath = string.IsNullOrWhiteSpace(environment.WebRootPath)
            ? Path.Combine(environment.ContentRootPath, "wwwroot")
            : environment.WebRootPath;
        _brandingAssetsDirectory = MagnetarPaths.GetQuasarBrandingDirectory(webRootPath);
    }

    /// <summary>Builds a backup archive in memory with a timestamped download name.</summary>
    public QuasarBackupArchive CreateBackup(DateTimeOffset timestamp)
    {
        var content = BuildArchiveBytes(timestamp);
        return new QuasarBackupArchive(content, BuildFileName(timestamp, automatic: false));
    }

    /// <summary>Writes a backup ZIP into the Backups directory (used by the scheduler).</summary>
    public async Task<string> WriteBackupFileAsync(DateTimeOffset timestamp, bool automatic, CancellationToken cancellationToken = default)
    {
        var content = BuildArchiveBytes(timestamp);
        var backupsDirectory = MagnetarPaths.GetQuasarBackupsDirectory();
        Directory.CreateDirectory(backupsDirectory);

        var path = CreateUniqueBackupPath(backupsDirectory, BuildFileName(timestamp, automatic));
        var tempPath = CreateTempBackupPath(path);
        try
        {
            await File.WriteAllBytesAsync(tempPath, content, cancellationToken);
            File.Move(tempPath, path);
        }
        finally
        {
            TryDeleteFile(tempPath);
        }

        _logger.LogInformation("Wrote configuration backup to {Path}", path);
        Changed?.Invoke();
        return path;
    }

    public Task<string> WriteServerBackupFileAsync(
        string uniqueName,
        DateTimeOffset timestamp,
        bool automatic = false,
        CancellationToken cancellationToken = default)
    {
        var definition = RequireServer(uniqueName);
        return WriteBackupFileAsync(
            BuildFileName(timestamp, automatic, QuasarBackupKind.Server, definition.UniqueName),
            archive => BuildServerArchive(archive, definition, timestamp, cancellationToken),
            cancellationToken);
    }

    public Task<string> WriteWorldBackupFileAsync(
        string uniqueName,
        DateTimeOffset timestamp,
        bool automatic = false,
        CancellationToken cancellationToken = default)
    {
        var definition = RequireServer(uniqueName);
        return WriteBackupFileAsync(
            BuildFileName(timestamp, automatic, QuasarBackupKind.World, definition.UniqueName),
            archive => BuildWorldArchive(archive, definition, timestamp, includeWorldConfig: false, cancellationToken),
            cancellationToken);
    }

    /// <summary>Deletes the oldest automatic backups beyond <paramref name="retentionCount"/>.</summary>
    public int PruneAutomaticBackups(int retentionCount)
    {
        return PruneAutomaticBackups(QuasarBackupKind.Configuration, retentionCount);
    }

    /// <summary>Deletes oldest automatic backups beyond <paramref name="retentionCount"/> for one backup kind.</summary>
    public int PruneAutomaticBackups(QuasarBackupKind kind, int retentionCount, string? serverUniqueName = null)
    {
        var backupsDirectory = MagnetarPaths.GetQuasarBackupsDirectory();
        if (!Directory.Exists(backupsDirectory))
            return 0;

        var automatic = ListBackups()
            .Where(backup => backup.Automatic && backup.Kind == kind)
            .Where(backup => string.IsNullOrWhiteSpace(serverUniqueName) ||
                string.Equals(backup.ServerUniqueName, serverUniqueName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(backup => backup.CreatedAtUtc)
            .Skip(Math.Max(0, retentionCount))
            .ToList();

        var deleted = 0;
        foreach (var backup in automatic)
        {
            var path = ResolveBackupPath(backup.Name);
            if (path is null)
                continue;

            try
            {
                File.Delete(path);
                deleted++;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Failed to prune old backup {Path}", path);
            }
        }

        if (deleted > 0)
            Changed?.Invoke();

        return deleted;
    }

    /// <summary>Ensures the Backups directory exists and deletes temporary files left by interrupted ZIP writes.</summary>
    public int CleanupIncompleteBackupFiles()
    {
        var backupsDirectory = MagnetarPaths.GetQuasarBackupsDirectory();
        try
        {
            Directory.CreateDirectory(backupsDirectory);

            var deleted = 0;
            foreach (var path in Directory.EnumerateFiles(backupsDirectory, "*.tmp"))
            {
                try
                {
                    File.Delete(path);
                    deleted++;
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(exception, "Failed to delete incomplete backup {Path}", path);
                }
            }

            if (deleted > 0)
                _logger.LogInformation("Deleted {Count} incomplete backup file(s).", deleted);

            return deleted;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to clean up incomplete backups in {Path}", backupsDirectory);
            return 0;
        }
    }

    public IReadOnlyList<QuasarBackupFileInfo> ListBackups()
    {
        var backupsDirectory = MagnetarPaths.GetQuasarBackupsDirectory();
        if (!Directory.Exists(backupsDirectory))
            return [];

        return Directory.EnumerateFiles(backupsDirectory, "*.zip")
            .Select(path =>
            {
                var info = new FileInfo(path);
                var manifest = TryReadManifest(path);
                var automatic = Path.GetFileNameWithoutExtension(path)
                    .EndsWith(AutomaticSuffix, StringComparison.OrdinalIgnoreCase);
                return new QuasarBackupFileInfo(
                    info.Name,
                    info.Length,
                    info.LastWriteTimeUtc,
                    manifest?.BackupKind ?? QuasarBackupKind.Configuration,
                    automatic,
                    manifest?.ServerUniqueName,
                    manifest?.ServerDisplayName);
            })
            .OrderByDescending(file => file.CreatedAtUtc)
            .ToList();
    }

    /// <summary>Resolves a backup file name to a full path inside the Backups directory, or null if invalid.</summary>
    public string? ResolveBackupPath(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        // Reject anything that is not a bare file name (defends the download endpoint).
        if (!string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal))
            return null;

        if (!fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            return null;

        var backupsDirectory = MagnetarPaths.GetQuasarBackupsDirectory();
        var fullPath = Path.GetFullPath(Path.Combine(backupsDirectory, fileName));
        if (!fullPath.StartsWith(EnsureTrailingSeparator(Path.GetFullPath(backupsDirectory)), StringComparison.Ordinal))
            return null;

        return File.Exists(fullPath) ? fullPath : null;
    }

    public bool DeleteBackup(string fileName)
    {
        var path = ResolveBackupPath(fileName);
        if (path is null)
            return false;

        File.Delete(path);
        Changed?.Invoke();
        return true;
    }

    public async Task<QuasarRestoreReport> RestoreFromFileAsync(string fileName, CancellationToken cancellationToken = default)
    {
        var path = ResolveBackupPath(fileName);
        if (path is null)
            return QuasarRestoreReport.Failed($"Backup '{fileName}' was not found.");

        await using var stream = File.OpenRead(path);
        return await RestoreAsync(stream, cancellationToken);
    }

    /// <summary>
    /// Restores a backup, merging it into the current configuration. Files are
    /// overwritten by their on-disk path, so configs/templates/servers with new IDs
    /// are added while matching IDs are replaced. Rejects archives whose version is
    /// incompatible per <see cref="BackupCompatibility"/>.
    /// </summary>
    public async Task<QuasarRestoreReport> RestoreAsync(Stream zipStream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(zipStream);

        var tempPath = Path.Combine(Path.GetTempPath(), $"quasar-restore-{Guid.NewGuid():N}.zip");
        try
        {
            await using (var tempWrite = File.Open(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await zipStream.CopyToAsync(tempWrite, cancellationToken);

            await using var tempRead = File.Open(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read);

            ZipArchive archive;
            try
            {
                archive = new ZipArchive(tempRead, ZipArchiveMode.Read, leaveOpen: true);
            }
            catch (InvalidDataException)
            {
                return QuasarRestoreReport.Failed("The selected file is not a valid ZIP archive.");
            }

            using (archive)
            {
                var manifestEntry = archive.GetEntry(ManifestEntryName);
                if (manifestEntry is null)
                    return QuasarRestoreReport.Failed("This ZIP is not a Quasar backup (missing quasar-backup.json).");

                QuasarBackupManifest? manifest;
                try
                {
                    await using var manifestStream = manifestEntry.Open();
                    manifest = await JsonSerializer.DeserializeAsync<QuasarBackupManifest>(manifestStream, JsonOptions, cancellationToken);
                }
                catch (JsonException)
                {
                    return QuasarRestoreReport.Failed("The backup manifest could not be read.");
                }

                if (manifest is null || string.IsNullOrWhiteSpace(manifest.QuasarVersion))
                    return QuasarRestoreReport.Failed("The backup manifest is missing version information.");

                var compatibility = BackupCompatibility.Evaluate(manifest.QuasarVersion, _options.Version);
                if (!compatibility.Allowed)
                    return QuasarRestoreReport.Failed(compatibility.Reason, manifest.QuasarVersion, _options.Version);

                return manifest.BackupKind switch
                {
                    QuasarBackupKind.Configuration => RestoreConfigurationArchive(archive, manifest, cancellationToken),
                    QuasarBackupKind.Server => await RestoreServerArchiveAsync(archive, manifest, cancellationToken),
                    QuasarBackupKind.World => await RestoreWorldArchiveAsync(archive, manifest, cancellationToken),
                    _ => QuasarRestoreReport.Failed($"Unsupported backup kind '{manifest.BackupKind}'.", manifest.QuasarVersion, _options.Version),
                };
            }
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    private QuasarRestoreReport RestoreConfigurationArchive(
        ZipArchive archive,
        QuasarBackupManifest manifest,
        CancellationToken cancellationToken)
    {
        var quasarRoot = Path.GetFullPath(MagnetarPaths.GetQuasarDirectory());
        var brandingRoot = Path.GetFullPath(_brandingAssetsDirectory);

        var restored = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.Equals(entry.FullName, ManifestEntryName, StringComparison.Ordinal))
                continue;

            // Directory entries have an empty Name.
            if (string.IsNullOrEmpty(entry.Name))
                continue;

            var target = ResolveConfigurationExtractionTarget(entry.FullName, quasarRoot, brandingRoot);
            if (target is null)
            {
                _logger.LogWarning("Skipping unexpected backup entry {Entry}", entry.FullName);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
            restored++;
        }

        // Catalogs without a file watcher need an explicit reload; watched ones
        // (servers, configs, templates, discord, branding, ...) reload themselves.
        _knownPlayers.ReloadFromDisk();
        _devFolders.ReloadFromDisk();

        _logger.LogInformation(
            "Restored {Count} files from a {BackupVersion} backup (running {RunningVersion}).",
            restored, manifest.QuasarVersion, _options.Version);

        return new QuasarRestoreReport
        {
            Success = true,
            FilesRestored = restored,
            BackupVersion = manifest.QuasarVersion,
            RunningVersion = _options.Version,
            RestartRecommended = true,
            Message = $"Restored {restored} configuration file(s). Restart Quasar to be sure every component picks up the change.",
        };
    }

    private byte[] BuildArchiveBytes(DateTimeOffset timestamp)
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
            BuildConfigurationArchive(archive, timestamp);

        return memory.ToArray();
    }

    private void BuildConfigurationArchive(ZipArchive archive, DateTimeOffset timestamp)
    {
        var quasarRoot = MagnetarPaths.GetQuasarDirectory();
        WriteManifest(archive, new QuasarBackupManifest
        {
            FormatVersion = CurrentFormatVersion,
            QuasarVersion = _options.Version,
            CreatedAtUtc = timestamp,
            CreatedByHost = _options.HostName,
            BackupKind = QuasarBackupKind.Configuration,
        });

        foreach (var fileName in SingletonConfigFiles)
        {
            var path = Path.Combine(quasarRoot, fileName);
            if (File.Exists(path))
                AddFile(archive, DataPrefix + fileName, path);
        }

        foreach (var (subdirectory, fileName) in DefinitionFiles)
        {
            var directory = Path.Combine(quasarRoot, subdirectory);
            if (!Directory.Exists(directory))
                continue;

            foreach (var path in Directory.EnumerateFiles(directory, fileName, SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(quasarRoot, path);
                AddFile(archive, DataPrefix + ToEntryPath(relative), path);
            }
        }

        if (Directory.Exists(_brandingAssetsDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(_brandingAssetsDirectory, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(_brandingAssetsDirectory, path);
                AddFile(archive, BrandingPrefix + ToEntryPath(relative), path);
            }
        }
    }

    private void BuildServerArchive(
        ZipArchive archive,
        DedicatedServerDefinition definition,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        WriteServerManifest(archive, definition, timestamp, QuasarBackupKind.Server);
        WriteEntry(archive, ServerDefinitionEntryName, JsonSerializer.SerializeToUtf8Bytes(definition, JsonOptions));

        var dedicatedServerAppDataRoot = Path.GetFullPath(definition.DedicatedServerAppDataPath);
        var worldRoot = Path.GetFullPath(definition.WorldPath);
        AddDirectory(
            archive,
            DedicatedServerPrefix,
            definition.DedicatedServerAppDataPath,
            cancellationToken,
            sourcePath => IsExcludedDedicatedServerBackupPath(dedicatedServerAppDataRoot, worldRoot, sourcePath));
        AddDirectory(
            archive,
            MagnetarPrefix,
            definition.MagnetarAppDataPath,
            cancellationToken,
            sourcePath => IsExcludedMagnetarBackupPath(definition.MagnetarAppDataPath, sourcePath));
        AddExternalDedicatedConfig(archive, definition, cancellationToken);
    }

    private void BuildWorldArchive(
        ZipArchive archive,
        DedicatedServerDefinition definition,
        DateTimeOffset timestamp,
        bool includeWorldConfig,
        CancellationToken cancellationToken)
    {
        WriteServerManifest(archive, definition, timestamp, QuasarBackupKind.World);
        WriteEntry(archive, ServerDefinitionEntryName, JsonSerializer.SerializeToUtf8Bytes(definition, JsonOptions));
        AddDirectory(
            archive,
            WorldPrefix,
            ResolveWorldSnapshotSource(definition.WorldPath),
            cancellationToken,
            includeWorldConfig ? null : IsWorldConfigPath);
    }

    private async Task<string> WriteBackupFileAsync(
        string fileName,
        Action<ZipArchive> buildArchive,
        CancellationToken cancellationToken)
    {
        var backupsDirectory = MagnetarPaths.GetQuasarBackupsDirectory();
        Directory.CreateDirectory(backupsDirectory);

        var path = CreateUniqueBackupPath(backupsDirectory, fileName);
        var tempPath = CreateTempBackupPath(path);
        try
        {
            await using (var stream = File.Open(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
                    buildArchive(archive);

                await stream.FlushAsync(cancellationToken);
            }

            File.Move(tempPath, path);
        }
        finally
        {
            TryDeleteFile(tempPath);
        }

        _logger.LogInformation("Wrote backup to {Path}", path);
        Changed?.Invoke();
        return path;
    }

    private async Task<QuasarRestoreReport> RestoreServerArchiveAsync(
        ZipArchive archive,
        QuasarBackupManifest manifest,
        CancellationToken cancellationToken)
    {
        var archivedDefinition = await ReadArchivedServerDefinitionAsync(archive, cancellationToken);
        var target = ResolveRestoreTarget(manifest, archivedDefinition, requireExisting: false);
        if (target is null)
            return QuasarRestoreReport.Failed("The server backup does not identify a target server.", manifest.QuasarVersion, _options.Version);

        if (!TryBeginServerRestore(target, manifest, "server", out var restoreScope, out var refusal))
            return refusal;

        using (restoreScope)
        {
            var restored = RestoreServerEntries(archive, target, includeWorldConfig: true, cancellationToken);
            _logger.LogInformation(
                "Restored {Count} files from server backup for {UniqueName}.",
                restored,
                target.UniqueName);

            return new QuasarRestoreReport
            {
                Success = true,
                FilesRestored = restored,
                BackupVersion = manifest.QuasarVersion,
                RunningVersion = _options.Version,
                Message = $"Restored {restored} server backup file(s) for {target.DisplayName}. Restart that server before relying on restored files.",
            };
        }
    }

    /// <summary>
    /// Refuses to restore a server/world backup over a server that is running (or
    /// transitioning), and otherwise claims a restore slot so the supervisor will
    /// not start the server while its files are being rewritten. On success
    /// <paramref name="restoreScope"/> must be disposed once the restore completes;
    /// on failure <paramref name="refusal"/> carries the user-facing reason.
    /// </summary>
    private bool TryBeginServerRestore(
        DedicatedServerDefinition target,
        QuasarBackupManifest manifest,
        string backupKindLabel,
        [NotNullWhen(true)] out IDisposable? restoreScope,
        [NotNullWhen(false)] out QuasarRestoreReport? refusal)
    {
        restoreScope = null;
        refusal = null;

        // Fast path: refuse outright if the server is already live.
        if (_supervisor.IsServerProcessActive(target.UniqueName))
        {
            refusal = BuildServerRunningRefusal(target, backupKindLabel, manifest);
            return false;
        }

        if (!_restoreCoordinator.TryBeginRestore(target.UniqueName, out var scope))
        {
            refusal = QuasarRestoreReport.Failed(
                $"A restore is already in progress for '{target.DisplayName}'. Wait for it to finish before restoring again.",
                manifest.QuasarVersion,
                _options.Version);
            return false;
        }

        // Re-check after claiming the slot: the supervisor only refuses a start
        // once the slot is held, so a start that raced in just before we claimed it
        // is caught here. Both the start and restore checks observe a consistent
        // ordering, so the two can never proceed at the same time.
        if (_supervisor.IsServerProcessActive(target.UniqueName))
        {
            scope.Dispose();
            refusal = BuildServerRunningRefusal(target, backupKindLabel, manifest);
            return false;
        }

        restoreScope = scope;
        return true;
    }

    private QuasarRestoreReport BuildServerRunningRefusal(
        DedicatedServerDefinition target,
        string backupKindLabel,
        QuasarBackupManifest manifest) =>
        QuasarRestoreReport.Failed(
            $"Server '{target.DisplayName}' is running. Stop it before restoring its {backupKindLabel} backup.",
            manifest.QuasarVersion,
            _options.Version);

    private async Task<QuasarRestoreReport> RestoreWorldArchiveAsync(
        ZipArchive archive,
        QuasarBackupManifest manifest,
        CancellationToken cancellationToken)
    {
        var archivedDefinition = await ReadArchivedServerDefinitionAsync(archive, cancellationToken);
        var target = ResolveRestoreTarget(manifest, archivedDefinition, requireExisting: true);
        if (target is null)
        {
            var uniqueName = manifest.ServerUniqueName ?? archivedDefinition?.UniqueName ?? "(unknown)";
            return QuasarRestoreReport.Failed(
                $"World backup targets server '{uniqueName}', but that server does not exist in Quasar.",
                manifest.QuasarVersion,
                _options.Version);
        }

        if (!TryBeginServerRestore(target, manifest, "world", out var restoreScope, out var refusal))
            return refusal;

        using (restoreScope)
        {
            var restored = RestoreWorldEntries(archive, target, includeWorldConfig: false, cancellationToken);
            _logger.LogInformation(
                "Restored {Count} world files from backup for {UniqueName}.",
                restored,
                target.UniqueName);

            return new QuasarRestoreReport
            {
                Success = true,
                FilesRestored = restored,
                BackupVersion = manifest.QuasarVersion,
                RunningVersion = _options.Version,
                Message = $"Restored {restored} world file(s) for {target.DisplayName}; existing world and server config was kept.",
            };
        }
    }

    private int RestoreServerEntries(
        ZipArchive archive,
        DedicatedServerDefinition target,
        bool includeWorldConfig,
        CancellationToken cancellationToken)
    {
        var restored = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(entry.Name) ||
                string.Equals(entry.FullName, ManifestEntryName, StringComparison.Ordinal))
            {
                continue;
            }

            string? destination = null;
            if (string.Equals(entry.FullName, ServerDefinitionEntryName, StringComparison.Ordinal))
                destination = MagnetarPaths.GetQuasarServerDefinitionPath(target.UniqueName);
            else if (entry.FullName.StartsWith(DedicatedServerPrefix, StringComparison.Ordinal))
                destination = ResolvePrefixedExtractionTarget(entry.FullName, DedicatedServerPrefix, target.DedicatedServerAppDataPath);
            else if (entry.FullName.StartsWith(DedicatedConfigPrefix, StringComparison.Ordinal))
                destination = ResolveDedicatedConfigExtractionTarget(entry.FullName, target);
            else if (entry.FullName.StartsWith(MagnetarPrefix, StringComparison.Ordinal))
                destination = ResolvePrefixedExtractionTarget(entry.FullName, MagnetarPrefix, target.MagnetarAppDataPath);
            else if (entry.FullName.StartsWith(WorldPrefix, StringComparison.Ordinal))
            {
                if (!includeWorldConfig && IsWorldConfigEntry(entry.FullName[WorldPrefix.Length..]))
                    continue;

                destination = ResolvePrefixedExtractionTarget(entry.FullName, WorldPrefix, target.WorldPath);
            }

            if (destination is null)
            {
                _logger.LogWarning("Skipping unexpected server backup entry {Entry}", entry.FullName);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
            restored++;
        }

        return restored;
    }

    private int RestoreWorldEntries(
        ZipArchive archive,
        DedicatedServerDefinition target,
        bool includeWorldConfig,
        CancellationToken cancellationToken)
    {
        var restored = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(entry.Name) || !entry.FullName.StartsWith(WorldPrefix, StringComparison.Ordinal))
                continue;

            var relative = entry.FullName[WorldPrefix.Length..];
            if (!includeWorldConfig && IsWorldConfigEntry(relative))
                continue;

            var destination = ResolvePrefixedExtractionTarget(entry.FullName, WorldPrefix, target.WorldPath);
            if (destination is null)
                continue;

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
            restored++;
        }

        return restored;
    }

    private static string? ResolveConfigurationExtractionTarget(string entryName, string quasarRoot, string brandingRoot)
    {
        string baseDirectory;
        string relative;

        if (entryName.StartsWith(DataPrefix, StringComparison.Ordinal))
        {
            baseDirectory = quasarRoot;
            relative = entryName[DataPrefix.Length..];
        }
        else if (entryName.StartsWith(BrandingPrefix, StringComparison.Ordinal))
        {
            baseDirectory = brandingRoot;
            relative = entryName[BrandingPrefix.Length..];
        }
        else
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(relative))
            return null;

        var fullTarget = Path.GetFullPath(Path.Combine(baseDirectory, relative));

        // Zip-slip guard: the resolved path must stay inside its base directory.
        if (!fullTarget.StartsWith(EnsureTrailingSeparator(baseDirectory), StringComparison.Ordinal))
            return null;

        return fullTarget;
    }

    private static string? ResolvePrefixedExtractionTarget(string entryName, string prefix, string baseDirectory)
    {
        if (!entryName.StartsWith(prefix, StringComparison.Ordinal))
            return null;

        var relative = entryName[prefix.Length..];
        if (string.IsNullOrWhiteSpace(relative))
            return null;

        var fullBase = Path.GetFullPath(baseDirectory);
        var fullTarget = Path.GetFullPath(Path.Combine(fullBase, relative));
        return IsPathWithinRoot(fullTarget, fullBase) ? fullTarget : null;
    }

    private static string? ResolveDedicatedConfigExtractionTarget(string entryName, DedicatedServerDefinition target)
    {
        if (!entryName.StartsWith(DedicatedConfigPrefix, StringComparison.Ordinal))
            return null;

        var relative = entryName[DedicatedConfigPrefix.Length..];
        if (!string.Equals(relative, "SpaceEngineers-Dedicated.cfg", StringComparison.OrdinalIgnoreCase))
            return null;

        var configPath = string.IsNullOrWhiteSpace(target.ConfigFilePath)
            ? Path.Combine(target.DedicatedServerAppDataPath, "SpaceEngineers-Dedicated.cfg")
            : target.ConfigFilePath.Trim();

        return Path.GetFullPath(configPath);
    }

    private DedicatedServerDefinition? ResolveRestoreTarget(
        QuasarBackupManifest manifest,
        DedicatedServerDefinition? archivedDefinition,
        bool requireExisting)
    {
        var uniqueName = manifest.ServerUniqueName ?? archivedDefinition?.UniqueName;
        if (string.IsNullOrWhiteSpace(uniqueName))
            return null;

        var current = _servers.GetServer(uniqueName);
        if (current is not null || requireExisting)
            return current;

        return archivedDefinition;
    }

    private async Task<DedicatedServerDefinition?> ReadArchivedServerDefinitionAsync(
        ZipArchive archive,
        CancellationToken cancellationToken)
    {
        var entry = archive.GetEntry(ServerDefinitionEntryName);
        if (entry is null)
            return null;

        try
        {
            await using var stream = entry.Open();
            return await JsonSerializer.DeserializeAsync<DedicatedServerDefinition>(stream, JsonOptions, cancellationToken);
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(exception, "Failed reading archived server definition from backup.");
            return null;
        }
    }

    private void WriteServerManifest(
        ZipArchive archive,
        DedicatedServerDefinition definition,
        DateTimeOffset timestamp,
        QuasarBackupKind kind) =>
        WriteManifest(archive, new QuasarBackupManifest
        {
            FormatVersion = CurrentFormatVersion,
            QuasarVersion = _options.Version,
            CreatedAtUtc = timestamp,
            CreatedByHost = _options.HostName,
            BackupKind = kind,
            ServerUniqueName = definition.UniqueName,
            ServerDisplayName = definition.DisplayName,
        });

    private static void WriteManifest(ZipArchive archive, QuasarBackupManifest manifest) =>
        WriteEntry(archive, ManifestEntryName, JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions));

    private DedicatedServerDefinition RequireServer(string uniqueName) =>
        _servers.GetServer(uniqueName) ?? throw new InvalidOperationException($"Unknown Quasar server '{uniqueName}'.");

    private void AddExternalDedicatedConfig(
        ZipArchive archive,
        DedicatedServerDefinition definition,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(definition.ConfigFilePath) || !File.Exists(definition.ConfigFilePath))
            return;

        var configPath = Path.GetFullPath(definition.ConfigFilePath);
        var appDataRoot = Path.GetFullPath(definition.DedicatedServerAppDataPath);
        if (IsPathWithinRoot(configPath, appDataRoot))
            return;

        cancellationToken.ThrowIfCancellationRequested();
        AddFile(archive, DedicatedConfigPrefix + "SpaceEngineers-Dedicated.cfg", configPath);
    }

    private static void AddDirectory(
        ZipArchive archive,
        string entryPrefix,
        string sourceDirectory,
        CancellationToken cancellationToken,
        Func<string, bool>? exclude = null)
    {
        if (string.IsNullOrWhiteSpace(sourceDirectory) || !Directory.Exists(sourceDirectory))
            return;

        foreach (var path in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (exclude?.Invoke(path) == true)
                continue;

            var relative = Path.GetRelativePath(sourceDirectory, path);
            AddFile(archive, entryPrefix + ToEntryPath(relative), path);
        }
    }

    private static bool IsExcludedDedicatedServerBackupPath(
        string dedicatedServerAppDataRoot,
        string worldRoot,
        string sourcePath)
    {
        var fullPath = Path.GetFullPath(sourcePath);
        if (IsPathWithinRoot(fullPath, worldRoot))
            return true;

        var relativePath = ToEntryPath(Path.GetRelativePath(dedicatedServerAppDataRoot, fullPath));
        return StartsWithEntrySegment(relativePath, DedicatedServerContentDirectory);
    }

    private static bool IsExcludedMagnetarBackupPath(string magnetarRoot, string sourcePath)
    {
        var relativePath = ToEntryPath(Path.GetRelativePath(magnetarRoot, sourcePath));
        if (StartsWithEntrySegment(relativePath, MagnetarGitHubDirectory) ||
            StartsWithEntrySegment(relativePath, MagnetarNuGetDirectory) ||
            StartsWithEntrySegment(relativePath, MagnetarPreloaderDirectory) ||
            StartsWithEntrySegments(relativePath, MagnetarSourcesDirectory, MagnetarSourcesPluginsDirectory))
        {
            return true;
        }

        if (!StartsWithEntrySegment(relativePath, MagnetarLocalDirectory))
            return false;

        var relativeLocalPath = relativePath.Length == MagnetarLocalDirectory.Length
            ? string.Empty
            : relativePath[(MagnetarLocalDirectory.Length + 1)..];
        return !relativeLocalPath.Contains('/') &&
            ExcludedMagnetarLocalFiles.Contains(relativeLocalPath);
    }

    private static bool StartsWithEntrySegment(string relativePath, string segment)
    {
        if (string.Equals(relativePath, segment, StringComparison.OrdinalIgnoreCase))
            return true;

        return relativePath.StartsWith(segment + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool StartsWithEntrySegments(string relativePath, string firstSegment, string secondSegment)
    {
        if (!StartsWithEntrySegment(relativePath, firstSegment))
            return false;

        var remainder = relativePath.Length == firstSegment.Length
            ? string.Empty
            : relativePath[(firstSegment.Length + 1)..];
        return StartsWithEntrySegment(remainder, secondSegment);
    }

    private static string ResolveWorldSnapshotSource(string worldPath)
    {
        var backupDirectory = Path.Combine(worldPath, "Backup");
        if (!Directory.Exists(backupDirectory))
            return worldPath;

        var latestBackup = Directory
            .EnumerateDirectories(backupDirectory)
            .Where(directory => File.Exists(Path.Combine(directory, "Sandbox.sbc")))
            .OrderByDescending(Directory.GetLastWriteTimeUtc)
            .FirstOrDefault();

        return latestBackup ?? worldPath;
    }

    private static void AddFile(ZipArchive archive, string entryName, string sourcePath)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var entryStream = entry.Open();
        using var fileStream = File.OpenRead(sourcePath);
        fileStream.CopyTo(entryStream);
    }

    private static void WriteEntry(ZipArchive archive, string entryName, byte[] content)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var entryStream = entry.Open();
        entryStream.Write(content, 0, content.Length);
    }

    private static QuasarBackupManifest? TryReadManifest(string path)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            var entry = archive.GetEntry(ManifestEntryName);
            if (entry is null)
                return null;

            using var stream = entry.Open();
            return JsonSerializer.Deserialize<QuasarBackupManifest>(stream, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static string BuildFileName(
        DateTimeOffset timestamp,
        bool automatic,
        QuasarBackupKind kind = QuasarBackupKind.Configuration,
        string? uniqueName = null)
    {
        var timestampText = timestamp.ToString("yyyyMMdd-HHmmss");
        return kind switch
        {
            QuasarBackupKind.Server => $"quasar-server-{SanitizeFileNameSegment(uniqueName)}-{timestampText}{(automatic ? AutomaticSuffix : string.Empty)}.zip",
            QuasarBackupKind.World => $"quasar-world-{SanitizeFileNameSegment(uniqueName)}-{timestampText}{(automatic ? AutomaticSuffix : string.Empty)}.zip",
            _ => $"quasar-backup-{timestampText}{(automatic ? AutomaticSuffix : string.Empty)}.zip",
        };
    }

    private static string CreateUniqueBackupPath(string backupsDirectory, string fileName)
    {
        var path = Path.Combine(backupsDirectory, fileName);
        if (!File.Exists(path))
            return path;

        var name = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var index = 2; ; index++)
        {
            var candidate = Path.Combine(backupsDirectory, $"{name}-{index}{extension}");
            if (!File.Exists(candidate))
                return candidate;
        }
    }

    private static string CreateTempBackupPath(string finalPath) =>
        $"{finalPath}.tmp";

    private static string ToEntryPath(string relativePath) =>
        relativePath.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');

    private static string EnsureTrailingSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;

    private static bool IsWorldConfigPath(string path) =>
        IsWorldConfigEntry(Path.GetFileName(path));

    private static bool IsWorldConfigEntry(string relativePath)
    {
        var fileName = Path.GetFileName(relativePath);
        return fileName.StartsWith("Sandbox_config.sbc", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPathWithinRoot(string fullPath, string fullRoot)
    {
        if (string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase))
            return true;

        return fullPath.StartsWith(EnsureTrailingSeparator(fullRoot), StringComparison.OrdinalIgnoreCase);
    }

    private static string SanitizeFileNameSegment(string? value)
    {
        var segment = string.IsNullOrWhiteSpace(value) ? "server" : value.Trim();
        foreach (var invalidCharacter in Path.GetInvalidFileNameChars())
            segment = segment.Replace(invalidCharacter, '-');

        return segment;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }
}
