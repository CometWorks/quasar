using System.IO.Compression;
using System.Text.Json;
using Magnetar.Protocol.Runtime;
using Quasar.Models;

namespace Quasar.Services.Backup;

/// <summary>An in-memory backup archive ready to stream to the browser.</summary>
public sealed record QuasarBackupArchive(byte[] Content, string FileName);

/// <summary>A backup ZIP found in the Backups directory.</summary>
public sealed record QuasarBackupFileInfo(string Name, long SizeBytes, DateTimeOffset CreatedAtUtc, bool Automatic);

/// <summary>
/// Builds and restores ZIP backups of Quasar's own configuration. The archive
/// contains a <c>quasar-backup.json</c> manifest plus a <c>data/</c> mirror of the
/// configuration files (singletons + server/config/world-template definitions) and
/// a <c>branding-assets/</c> copy of the uploaded logo/favicon images. Game
/// servers, worlds, plugin configurations, runtime state, logs and history are
/// deliberately excluded — see the allow-list below.
/// </summary>
public sealed class QuasarBackupService
{
    public const int CurrentFormatVersion = 1;

    private const string ManifestEntryName = "quasar-backup.json";
    private const string DataPrefix = "data/";
    private const string BrandingPrefix = "branding-assets/";
    private const string AutomaticSuffix = "-auto";

    // Singleton config files living directly in the Quasar root (included if present).
    private static readonly string[] SingletonConfigFiles =
    [
        "known-players.json",
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
    private readonly string _brandingAssetsDirectory;

    public QuasarBackupService(
        ILogger<QuasarBackupService> logger,
        WebServiceOptions options,
        IWebHostEnvironment environment,
        KnownPlayerCatalog knownPlayers,
        QuasarDevFolderCatalog devFolders)
    {
        _logger = logger;
        _options = options;
        _knownPlayers = knownPlayers;
        _devFolders = devFolders;

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

        var path = Path.Combine(backupsDirectory, BuildFileName(timestamp, automatic));
        await File.WriteAllBytesAsync(path, content, cancellationToken);
        _logger.LogInformation("Wrote configuration backup to {Path}", path);
        return path;
    }

    /// <summary>Deletes the oldest automatic backups beyond <paramref name="retentionCount"/>.</summary>
    public int PruneAutomaticBackups(int retentionCount)
    {
        var backupsDirectory = MagnetarPaths.GetQuasarBackupsDirectory();
        if (!Directory.Exists(backupsDirectory))
            return 0;

        var automatic = Directory
            .EnumerateFiles(backupsDirectory, $"*{AutomaticSuffix}.zip")
            .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase) // timestamped name sorts chronologically
            .Skip(Math.Max(0, retentionCount))
            .ToList();

        var deleted = 0;
        foreach (var path in automatic)
        {
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

        return deleted;
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
                var automatic = Path.GetFileNameWithoutExtension(path)
                    .EndsWith(AutomaticSuffix, StringComparison.OrdinalIgnoreCase);
                return new QuasarBackupFileInfo(info.Name, info.Length, info.LastWriteTimeUtc, automatic);
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

        // ZipArchive in Read mode needs a seekable stream; browser upload streams are not.
        await using var buffer = new MemoryStream();
        await zipStream.CopyToAsync(buffer, cancellationToken);
        buffer.Position = 0;

        ZipArchive archive;
        try
        {
            archive = new ZipArchive(buffer, ZipArchiveMode.Read, leaveOpen: true);
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

                var target = ResolveExtractionTarget(entry.FullName, quasarRoot, brandingRoot);
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
            // (servers, configs, templates, discord, branding, …) reload themselves.
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
    }

    private byte[] BuildArchiveBytes(DateTimeOffset timestamp)
    {
        var quasarRoot = MagnetarPaths.GetQuasarDirectory();

        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifest = new QuasarBackupManifest
            {
                FormatVersion = CurrentFormatVersion,
                QuasarVersion = _options.Version,
                CreatedAtUtc = timestamp,
                CreatedByHost = _options.HostName,
            };
            WriteEntry(archive, ManifestEntryName, JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions));

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

        return memory.ToArray();
    }

    private static string? ResolveExtractionTarget(string entryName, string quasarRoot, string brandingRoot)
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

    private static string BuildFileName(DateTimeOffset timestamp, bool automatic) =>
        $"quasar-backup-{timestamp:yyyyMMdd-HHmmss}{(automatic ? AutomaticSuffix : string.Empty)}.zip";

    private static string ToEntryPath(string relativePath) =>
        relativePath.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');

    private static string EnsureTrailingSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;
}
