using System.IO.Compression;
using System.Xml;

namespace Quasar.Services;

public sealed record WorldImportProgress(string Phase, long Bytes, int CompletedFiles = 0, int TotalFiles = 0);

/// <summary>Owned, private world files. Disposal removes staging, never a published save.</summary>
public sealed class StagedWorld : IDisposable
{
    private readonly string _root;
    public string WorldPath { get; }
    public string SuggestedName { get; }
    internal bool Published { get; private set; }

    internal StagedWorld(string root, string worldPath, string suggestedName)
        => (_root, WorldPath, SuggestedName) = (root, worldPath, suggestedName);

    internal void Publish(string destination)
    {
        if (Published) throw new InvalidOperationException("This world was already imported.");
        WorldSaveFiles.EnsureNoLinks(Path.GetDirectoryName(destination)!);
        if (Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(destination)!).Any(path =>
                Path.GetFileName(path).Equals(Path.GetFileName(destination), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("A save with that name already exists. Choose another folder name.");
        Directory.Move(WorldPath, destination); // Atomic, and refuses an existing destination.
        Published = true;
    }

    public void Dispose() => WorldSaveFiles.Cleanup(_root);
}

/// <summary>Raw save ingestion shared by server imports and reusable templates.</summary>
public static class WorldSaveFiles
{
    public const long MaxUploadBytes = 10L * 1024 * 1024 * 1024;
    public const long MaxWorldBytes = 50L * 1024 * 1024 * 1024;
    public const int MaxEntries = 100_000;

    public static Task<StagedWorld> FromFolderAsync(string source, string stagingParent,
        IProgress<WorldImportProgress>? progress = null, CancellationToken cancellationToken = default)
        => Task.Run(async () =>
        {
            source = Path.GetFullPath(source.Trim());
            stagingParent = Path.GetFullPath(stagingParent);
            EnsureNoLinks(source);
            if (!Directory.Exists(source)) throw new InvalidOperationException("Source world folder not found.");
            if (Models.DedicatedServerPathResolver.IsPathWithinRoot(stagingParent, source))
                throw new InvalidOperationException("The destination cannot be inside the source world.");
            ValidateWorld(source, cancellationToken);
            string root = CreateStagingRoot(stagingParent);
            try
            {
                string world = Path.Combine(root, "World");
                Directory.CreateDirectory(world);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var paths = new List<(string Source, string Relative, bool Directory)>();
                int entries = 0;
                ScanDirectory(source, "");
                int totalFiles = paths.Count(item => !item.Directory);
                int completed = 0;
                long bytes = 0;
                progress?.Report(new("Copying world", bytes, completed, totalFiles));
                foreach (var item in paths)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string destination = Path.Combine(world, item.Relative.Replace('/', Path.DirectorySeparatorChar));
                    if (item.Directory) { Directory.CreateDirectory(destination); continue; }
                    await using var input = new FileStream(item.Source, FileMode.Open, FileAccess.Read, FileShare.Read);
                    await using var output = OpenPrivateFile(destination);
                    bytes += await CopyBoundedAsync(input, output, MaxWorldBytes - bytes,
                        copied => progress?.Report(new("Copying world", bytes + copied, completed, totalFiles)), cancellationToken);
                    progress?.Report(new("Copying world", bytes, ++completed, totalFiles));
                }
                cancellationToken.ThrowIfCancellationRequested();
                ValidateWorld(world, cancellationToken);
                return new StagedWorld(root, world, Path.GetFileName(source));

                void ScanDirectory(string directory, string prefix)
                {
                    foreach (string path in Directory.EnumerateFileSystemEntries(directory))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (++entries > MaxEntries) throw new InvalidOperationException("World has too many files or folders.");
                        string name = Path.GetFileName(path);
                        if (prefix.Length == 0 && name.Equals("Backup", StringComparison.OrdinalIgnoreCase)) continue;
                        string relative = prefix + name;
                        ValidateRelativePath(relative);
                        if (!seen.Add(relative)) throw new InvalidOperationException($"Duplicate world path: {relative}");
                        EnsureNoLinks(path);
                        bool isDirectory = Directory.Exists(path);
                        paths.Add((path, relative, isDirectory));
                        if (isDirectory) ScanDirectory(path, relative + "/");
                    }
                }
            }
            catch { Cleanup(root); throw; }
        }, cancellationToken);

    public static async Task<StagedWorld> FromZipAsync(Stream input, string stagingParent,
        IProgress<WorldImportProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        string root = CreateStagingRoot(stagingParent);
        try
        {
            string zipPath = Path.Combine(root, "upload.zip");
            await using (var output = OpenPrivateFile(zipPath))
                await CopyBoundedAsync(input, output, MaxUploadBytes,
                    bytes => progress?.Report(new("Uploading ZIP", bytes)), cancellationToken);
            // ZIP enumeration and XML validation must not block the Blazor circuit.
            return await Task.Run(async () =>
            {
                using var zipInput = File.OpenRead(zipPath);
                using var archive = new ZipArchive(zipInput, ZipArchiveMode.Read);
                var entries = ReadZipEntries(archive, cancellationToken);
                if (entries.Sum(item => item.Entry.Length) > MaxWorldBytes)
                    throw new InvalidOperationException("Extracted ZIP exceeds 50 GiB.");
                var candidates = entries.Where(item => !item.Path.Split('/').Any(part => part.Equals("Backup", StringComparison.OrdinalIgnoreCase))
                    && Path.GetFileName(item.Path).Equals("Sandbox.sbc", StringComparison.OrdinalIgnoreCase)).ToList();
                if (candidates.Count != 1) throw new InvalidOperationException("ZIP must contain exactly one world with Sandbox.sbc, outside Backup folders.");
                string prefix = candidates[0].Path[..^"Sandbox.sbc".Length];
                string world = Path.Combine(root, "World");
                Directory.CreateDirectory(world);
                long bytes = 0;
                foreach (var (entry, path) in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!path.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    string relative = path[prefix.Length..];
                    if (relative.Length == 0 || relative.Split('/')[0].Equals("Backup", StringComparison.OrdinalIgnoreCase)) continue;
                    string target = Path.Combine(world, relative.Replace('/', Path.DirectorySeparatorChar));
                    if (path.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
                    bytes += await CopyZipEntryAsync(entry, zipInput, target, MaxWorldBytes - bytes,
                        count => progress?.Report(new("Extracting ZIP", bytes + count)), cancellationToken);
                }
                cancellationToken.ThrowIfCancellationRequested();
                ValidateWorld(world, cancellationToken);
                return new StagedWorld(root, world, prefix.Length == 0 ? "Imported World" : prefix.TrimEnd('/').Split('/')[^1]);
            }, cancellationToken);
        }
        catch { Cleanup(root); throw; }
    }

    internal static List<(ZipArchiveEntry Entry, string Path)> ReadZipEntries(ZipArchive archive, CancellationToken cancellationToken)
    {
        if (archive.Entries.Count > MaxEntries) throw new InvalidOperationException("ZIP has too many entries.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var portablePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<(ZipArchiveEntry Entry, string Path)>();
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = entry.FullName.Replace('\\', '/');
            ValidateRelativePath(path.TrimEnd('/'));
            RegisterPortablePath(path.TrimEnd('/'), portablePaths);
            if (!seen.Add(path.TrimEnd('/'))) throw new InvalidOperationException($"Duplicate ZIP path: {path}");
            int kind = (entry.ExternalAttributes >> 16) & 0xF000;
            if (kind is not (0 or 0x8000 or 0x4000) || (entry.ExternalAttributes & 0x400) != 0)
                throw new InvalidOperationException("ZIP links and special files are not supported.");
            if (!path.EndsWith('/')) files.Add(path);
            entries.Add((entry, path));
        }
        foreach (var (_, path) in entries)
        {
            string? parent = Path.GetDirectoryName(path.TrimEnd('/'));
            while (!string.IsNullOrEmpty(parent))
            {
                if (files.Contains(parent.Replace('\\', '/')))
                    throw new InvalidOperationException("ZIP contains conflicting file and folder paths.");
                parent = Path.GetDirectoryName(parent);
            }
        }
        return entries;
    }

    internal static async Task<long> CopyZipEntryAsync(ZipArchiveEntry entry, Stream archiveStream, string destination,
        long limit, Action<long> progress, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await using var source = entry.Open();
        if (archiveStream is BrowserZipFileStream browser) browser.BeginEntry(entry.CompressedLength);
        await using var output = OpenPrivateFile(destination);
        long copied = await CopyBoundedAsync(source, output, limit, progress, token);
        if (copied != entry.Length) throw new InvalidOperationException("ZIP entry size does not match its contents.");
        return copied;
    }

    public static void ValidateSaveName(string name)
    {
        ValidateRelativePath(name);
        if (name.Contains('/') || name.Contains('\\') || name.StartsWith('.'))
            throw new InvalidOperationException("Save folder name must be a single, visible folder name.");
    }

    private static void ValidateRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith('/') || path.Contains('\\'))
            throw new InvalidOperationException("Invalid world path.");
        foreach (string part in path.Split('/'))
        {
            string stem = part.Split('.')[0];
            if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ')
                || part.Any(ch => ch < 32 || "<>:\"|?*".Contains(ch))
                || new[] { "CON", "PRN", "AUX", "NUL" }.Contains(stem, StringComparer.OrdinalIgnoreCase)
                || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                    || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && stem[3] is >= '0' and <= '9'))
                throw new InvalidOperationException($"Unsafe world path: {path}");
        }
    }

    private static void RegisterPortablePath(string path, Dictionary<string, string> names)
    {
        string prefix = "";
        foreach (string part in path.Split('/'))
        {
            prefix = prefix.Length == 0 ? part : prefix + "/" + part;
            if (names.TryGetValue(prefix, out string? previous) && previous != prefix)
                throw new InvalidOperationException($"ZIP paths differ only in letter case: {previous}, {prefix}");
            names[prefix] = prefix;
        }
    }

    internal static void EnsureNoLinks(string path)
    {
        for (FileSystemInfo? item = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            item is not null; item = item is DirectoryInfo directory ? directory.Parent : ((FileInfo)item).Directory)
            if (item.LinkTarget is not null || (item.Exists && (item.Attributes & FileAttributes.ReparsePoint) != 0))
                throw new InvalidOperationException("World import does not support symbolic links or junctions.");
    }

    internal static string CreateStagingRoot(string parent)
    {
        parent = Path.GetFullPath(parent);
        EnsureNoLinks(parent);
        Directory.CreateDirectory(parent);
        string root = Path.Combine(parent, ".world-import-" + Guid.NewGuid().ToString("N"));
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(root);
        else Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return root;
    }

    private static FileStream OpenPrivateFile(string path)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write,
            Share = FileShare.None, Options = FileOptions.Asynchronous };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(path, options);
    }

    private static async Task<long> CopyBoundedAsync(Stream source, Stream destination, long limit,
        Action<long> progress, CancellationToken token)
    {
        byte[] buffer = new byte[128 * 1024];
        long total = 0;
        while (true)
        {
            int count = await source.ReadAsync(buffer, token);
            if (count == 0) break;
            total += count;
            if (total > limit) throw new InvalidOperationException("World import size limit exceeded (10 GiB ZIP, 50 GiB extracted world).");
            await destination.WriteAsync(buffer.AsMemory(0, count), token);
            progress(total);
        }
        return total;
    }

    internal static void ValidateWorld(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string checkpoint = Path.Combine(path, "Sandbox.sbc");
        if (!File.Exists(checkpoint)) throw new InvalidOperationException("World folder must contain Sandbox.sbc with that spelling.");
        using var reader = XmlReader.Create(checkpoint, new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 256L * 1024 * 1024 });
        reader.MoveToContent();
        if (reader.LocalName != "MyObjectBuilder_Checkpoint") throw new InvalidOperationException("Sandbox.sbc is not a Space Engineers world checkpoint.");
        while (reader.Read()) token.ThrowIfCancellationRequested();
    }

    internal static void Cleanup(string root)
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        catch (IOException) { } // An unselected private directory can be removed later.
        catch (UnauthorizedAccessException) { }
    }
}
