using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Magnetar.Protocol.Runtime;
using Quasar.Models;
using Quasar.ClusterDeployment;

namespace Quasar.Services;

/// <summary>Copies approved dependency bytes into an isolated, immutable installation. Never starts or updates a runtime.</summary>
public sealed class ClusterDependencyService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const int MaxFiles = 200_000;
    private const long MaxBytes = 100L * 1024 * 1024 * 1024;
    private readonly Func<Dictionary<string, string>> _sources;
    private readonly string _directory;
    private readonly ClusterPackageService _packages;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ClusterDependencyService(ManagedDedicatedServerRuntimeResolver runtime, IConfiguration configuration,
        ClusterPackageService packages)
        : this(() => ResolveSources(runtime, configuration),
            Path.Combine(MagnetarPaths.GetQuasarManagedRuntimeToolsDirectory(), "ClusterDependencies"), packages) { }

    internal ClusterDependencyService(Func<Dictionary<string, string>> sources, string directory, ClusterPackageService packages)
        => (_sources, _directory, _packages) = (sources, directory, packages);

    public async Task<ClusterDependencyCandidate> InspectAsync(ClusterDefinition cluster, CancellationToken token)
        => await InspectAsync(cluster, null, token);

    internal async Task<ClusterDependencyCandidate> InspectAsync(ClusterDefinition cluster, Dictionary<string, string>? sources, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            var selected = await VerifyPackageAsync(cluster, token);
            var manifest = await InspectSourcesAsync(selected, ResolveSourcePaths(sources), token);
            return Candidate(selected.Revision, manifest);
        }
        finally { _gate.Release(); }
    }

    public async Task<ClusterDependencyCandidate> StageAsync(ClusterDefinition cluster,
        ClusterDependencyRequest request, CancellationToken token)
        => await StageAsync(cluster, request, null, token);

    internal async Task<ClusterDependencyCandidate> StageAsync(ClusterDefinition cluster,
        ClusterDependencyRequest request, Dictionary<string, string>? preparedSources, CancellationToken token)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Cluster dependency provisioning requires Linux.");
        ValidateRequest(request);
        await _gate.WaitAsync(token);
        string? staging = null;
        try
        {
            var selected = await VerifyPackageAsync(cluster, token);
            if (selected.Revision != request.ExpectedPackageRevision)
                throw new ClusterOperationConflictException(409, "package_selection_conflict", "Package selection changed.");
            string destination = Path.Combine(_directory, request.ManifestSha256);
            if (Directory.Exists(destination))
                return await VerifyAsyncCore(selected, request.ManifestSha256, token);

            var sources = ResolveSourcePaths(preparedSources);
            var manifest = await InspectSourcesAsync(selected, sources, token);
            var candidate = Candidate(selected.Revision, manifest);
            if (candidate.ManifestSha256 != request.ManifestSha256)
                throw new InvalidDataException("Dependency inputs changed; inspect and approve their new manifest hash.");
            Directory.CreateDirectory(_directory);
            RefuseLink(_directory);
            staging = Path.Combine(_directory, ".stage-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(staging, "payload"));
            foreach (var (relative, pin) in manifest.Files)
            {
                token.ThrowIfCancellationRequested();
                var source = sources.Single(pair => relative.StartsWith(pair.Key + "/", StringComparison.Ordinal));
                string sourcePath = Path.Combine(source.Value, relative[(source.Key.Length + 1)..]);
                RefuseLink(source.Value);
                // Recheck directories too: do not follow a link substituted since inspection.
                string current = source.Value;
                foreach (string part in relative[(source.Key.Length + 1)..].Split('/'))
                {
                    current = Path.Combine(current, part);
                    RefuseLink(current);
                }
                string target = Path.Combine(staging, "payload", relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await CopyVerifiedAsync(sourcePath, target, pin, token);
            }
            await File.WriteAllBytesAsync(Path.Combine(staging, "manifest.json"), Serialize(manifest), token);
            // A same-filesystem rename publishes all dependencies together; never overwrite a pin.
            Directory.Move(staging, destination);
            staging = null;
            return candidate;
        }
        finally
        {
            try { if (staging is not null) Directory.Delete(staging, true); }
            finally { _gate.Release(); }
        }
    }

    public async Task<ClusterDependencyCandidate> VerifyAsync(ClusterDefinition cluster, string hash, CancellationToken token)
    {
        ValidateHash(hash);
        await _gate.WaitAsync(token);
        try { return await VerifyAsyncCore(await VerifyPackageAsync(cluster, token), hash, token); }
        finally { _gate.Release(); }
    }

    public async Task<global::Quasar.Host.Contract.V1.ClusterDeploymentInputs> GetDeploymentInputsAsync(
        ClusterDefinition cluster, CancellationToken token)
    {
        string hash = cluster.DependencyManifestSha256 ?? throw new InvalidDataException("Select a dependency snapshot first.");
        ValidateHash(hash);
        await _gate.WaitAsync(token);
        try
        {
            var selected = await VerifyPackageAsync(cluster, token);
            await VerifyAsyncCore(selected, hash, token);
            var package = await _packages.GetInstalledAsync(new(selected.Version, selected.Sha256), token);
            ClusterDeploymentFiles.ValidateCapabilities(package.PackagePath);
            string dependencies = Path.Combine(_directory, hash);
            var packageFiles = await ClusterDeploymentFiles.InspectAsync(package.PackagePath, token);
            if (packageFiles.Count != package.Files.Count || packageFiles.Any(x =>
                    !package.Files.TryGetValue(x.Key, out string? pin) || pin != x.Value.Sha256))
                throw new InvalidDataException("Package changed during deployment preparation.");
            var files = packageFiles.ToDictionary(x => "Package/" + x.Key, x => x.Value);
            foreach (var (path, pin) in await ClusterDeploymentFiles.InspectAsync(dependencies, token))
            {
                if (path != "manifest.json" && !path.StartsWith("payload/", StringComparison.Ordinal))
                    throw new InvalidDataException("Dependency snapshot contains an unexpected entry.");
                files.Add("Dependencies/" + path, pin);
            }
            if (files["Dependencies/manifest.json"].Sha256 != hash)
                throw new InvalidDataException("Dependency manifest changed during deployment preparation.");
            return new(cluster.UniqueName, selected.Revision, selected.Version, selected.Sha256, selected.Commit,
                hash, package.PackagePath, dependencies, files);
        }
        finally { _gate.Release(); }
    }

    private async Task<ClusterDependencyCandidate> VerifyAsyncCore(ClusterPackageSelection selected, string hash,
        CancellationToken token)
    {
        string root = Path.Combine(_directory, hash);
        string receipt = Path.Combine(root, "manifest.json");
        RefuseLink(_directory);
        RefuseLink(root);
        RefuseLink(receipt);
        byte[] bytes = await File.ReadAllBytesAsync(receipt, token);
        if (Hash(bytes) != hash) throw new InvalidDataException("Dependency manifest changed.");
        var manifest = JsonSerializer.Deserialize<ClusterDependencyManifest>(bytes, JsonOptions)
            ?? throw new InvalidDataException("Dependency manifest is empty.");
        if (manifest.SchemaVersion != 2 || manifest.PackageVersion != selected.Version
            || manifest.PackageSha256 != selected.Sha256 || manifest.PackageCommit != selected.Commit
            || !ClusterPackageService.IsHash(manifest.DirectTransportCommit, 40) || manifest.Files is null)
            throw new InvalidDataException("Dependency manifest does not match the selected cluster package.");
        string payload = Path.Combine(root, "payload");
        var files = await ScanAsync(new() { [""] = payload }, token);
        if (files.Count != manifest.Files.Count || files.Any(pair =>
                !manifest.Files.TryGetValue(pair.Key, out var pin) || pair.Value != pin))
            throw new InvalidDataException("Provisioned dependency files changed.");
        ValidateLayout(files);
        ClusterPluginBundles.Validate(Path.Combine(payload, "CommonPlugins"));
        if (ReadTransportCommit(Path.Combine(payload, "DirectTransport")) != manifest.DirectTransportCommit)
            throw new InvalidDataException("Direct Transport provenance does not match the dependency manifest.");
        return Candidate(selected.Revision, manifest);
    }

    private async Task<ClusterPackageSelection> VerifyPackageAsync(ClusterDefinition cluster, CancellationToken token)
    {
        var selected = cluster.PackageSelection ?? throw new InvalidDataException("Select a cluster package first.");
        var package = await _packages.GetInstalledAsync(new(selected.Version, selected.Sha256), token);
        if (package.Commit != selected.Commit) throw new InvalidDataException("Selected package commit changed.");
        return selected;
    }

    private Dictionary<string, string> ResolveSourcePaths(Dictionary<string, string>? preparedSources = null)
    {
        var paths = preparedSources is null ? _sources() : new Dictionary<string, string>(preparedSources);
        string[] required = ["DedicatedServer/DedicatedServer64", "DedicatedServer/Content", "Magnetar", "DirectTransport", "CommonPlugins"];
        if (paths.Count != required.Length || required.Any(key => !paths.ContainsKey(key)))
            throw new InvalidDataException("DS, Content, Magnetar, Direct Transport and common plugin bundles are required.");
        foreach (string key in required)
        {
            if (string.IsNullOrWhiteSpace(paths[key])) throw new InvalidDataException($"Dependency source {key} is not configured.");
            paths[key] = Path.GetFullPath(paths[key]);
            RefuseLink(paths[key]);
            if (!Directory.Exists(paths[key])) throw new InvalidDataException($"Dependency source {key} is not a directory.");
            string storage = Path.GetFullPath(_directory);
            if (storage == paths[key] || storage.StartsWith(paths[key] + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidDataException("Dependency storage cannot be inside an input directory.");
        }
        return paths;
    }

    private static Dictionary<string, string> ResolveSources(ManagedDedicatedServerRuntimeResolver runtime, IConfiguration config)
    {
        string ds64 = runtime.ResolveInstalledDedicatedServer64Path();
        return new()
        {
            ["DedicatedServer/DedicatedServer64"] = ds64,
            ["DedicatedServer/Content"] = string.IsNullOrEmpty(ds64) ? "" : Path.Combine(Path.GetDirectoryName(ds64)!, "Content"),
            ["Magnetar"] = runtime.ResolveInstalledMagnetarInstallDirectory(),
            ["DirectTransport"] = config["Quasar:ClusterDependencies:DirectTransportDirectory"] ?? "",
            ["CommonPlugins"] = config["Quasar:ClusterDependencies:CommonPluginsDirectory"] ?? "",
        };
    }

    private static async Task<ClusterDependencyManifest> InspectSourcesAsync(ClusterPackageSelection selected,
        Dictionary<string, string> paths, CancellationToken token)
    {
        var files = await ScanAsync(paths, token);
        ValidateLayout(files);
        string commit = ReadTransportCommit(paths["DirectTransport"]);
        ClusterPluginBundles.Validate(paths["CommonPlugins"]);
        return new(2, selected.Version, selected.Sha256, selected.Commit, commit, files);
    }

    private static void ValidateLayout(Dictionary<string, ClusterDependencyFile> files)
    {
        string[] required = ["DedicatedServer/DedicatedServer64/SpaceEngineersDedicated.exe",
            "DedicatedServer/DedicatedServer64/SpaceEngineers.Game.dll", "DedicatedServer/DedicatedServer64/Sandbox.Game.dll",
            "DedicatedServer/DedicatedServer64/VRage.dll", "Magnetar/MagnetarInterim.bin",
            "Magnetar/Libraries/MagnetarInterim/PluginSdk.dll", "Magnetar/Libraries/MagnetarInterim/libsteam_api.so", "DirectTransport/DirectTransport.dll",
            "DirectTransport/DirectTransport.xml", "DirectTransport/DirectTransport.dll.xml", "DirectTransport/LiteNetLib.dll"];
        if (required.Any(path => !files.TryGetValue(path, out var pin) || pin.Bytes == 0)
            || !files.Keys.Any(path => path.StartsWith("DedicatedServer/Content/", StringComparison.Ordinal)))
            throw new InvalidDataException("Dependencies are incomplete; supply DS with Content, current Linux Magnetar and compiled Direct Transport with metadata and LiteNetLib.");
        if (!files["Magnetar/MagnetarInterim.bin"].Executable)
            throw new InvalidDataException("Magnetar launcher is not executable.");
    }

    private static string ReadTransportCommit(string root)
    {
        string? commit = null;
        foreach (string name in new[] { "DirectTransport.xml", "DirectTransport.dll.xml" })
        {
            string path = Path.Combine(root, name);
            RefuseLink(path);
            using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                MaxCharactersInDocument = 1024 * 1024 });
            var data = XDocument.Load(reader).Root;
            string? value = data?.Element("Commit")?.Value;
            if (data?.Element("Id")?.Value != "direct-transport" || !ClusterPackageService.IsHash(value, 40)
                || (commit is not null && value != commit))
                throw new InvalidDataException("Direct Transport metadata must declare direct-transport and the same exact source commit.");
            if (data.Element("Runtimes")?.Value != "CoreCLR" || data.Element("Platforms")?.Value != "Linux")
                throw new InvalidDataException("Compiled Direct Transport metadata must target CoreCLR on Linux.");
            commit = value;
        }
        return commit!;
    }

    private static async Task<Dictionary<string, ClusterDependencyFile>> ScanAsync(Dictionary<string, string> roots,
        CancellationToken token)
    {
        var files = new SortedDictionary<string, ClusterDependencyFile>(StringComparer.Ordinal);
        long bytes = 0;
        foreach (var (prefix, root) in roots)
        {
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.TryPop(out string? current))
            {
                token.ThrowIfCancellationRequested();
                RefuseLink(current);
                if (Directory.Exists(current))
                {
                    foreach (string child in Directory.EnumerateFileSystemEntries(current)) pending.Push(child);
                    continue;
                }
                string relative = Path.GetRelativePath(root, current).Replace(Path.DirectorySeparatorChar, '/');
                if (relative.Split('/').Any(part => part is "" or "." or "..") || relative.Contains('\\'))
                    throw new InvalidDataException("Dependency contains an unsupported path.");
                long length = new FileInfo(current).Length;
                if (length > MaxBytes - bytes || files.Count >= MaxFiles)
                    throw new InvalidDataException("Dependency input exceeds supported limits.");
                bytes += length;
                await using var input = File.OpenRead(current);
                string hash = Convert.ToHexString(await SHA256.HashDataAsync(input, token)).ToLowerInvariant();
                if (input.Length != length) throw new InvalidDataException("Dependency input changed during inspection.");
                bool executable = !OperatingSystem.IsWindows()
                    && (File.GetUnixFileMode(current) & UnixFileMode.UserExecute) != 0;
                files.Add(prefix.Length == 0 ? relative : prefix + "/" + relative, new(hash, length, executable));
            }
        }
        return files.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    }

    private static async Task CopyVerifiedAsync(string source, string destination, ClusterDependencyFile pin, CancellationToken token)
    {
        await using var input = File.OpenRead(source);
        await using var output = File.Create(destination);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[81920];
        long copied = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, token)) != 0)
        {
            copied += read;
            if (copied > pin.Bytes) throw new InvalidDataException("Dependency input changed during provisioning.");
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), token);
        }
        if (copied != pin.Bytes || Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() != pin.Sha256)
            throw new InvalidDataException("Dependency input changed during provisioning.");
        await output.FlushAsync(token);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(destination, UnixFileMode.UserRead | UnixFileMode.UserWrite
                | UnixFileMode.GroupRead | UnixFileMode.OtherRead
                | (pin.Executable ? UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute : 0));
    }

    private static void RefuseLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Dependency trees must not contain symbolic links.");
    }

    internal static void ValidateRequest(ClusterDependencyRequest request)
    {
        ValidateHash(request.ManifestSha256);
        if (request.ExpectedPackageRevision is null or <= 0)
            throw new InvalidDataException("A positive expectedPackageRevision is required.");
        if (request.ExpectedDependencySha256 is not null) ValidateHash(request.ExpectedDependencySha256);
    }

    private static void ValidateHash(string? hash)
    {
        if (!ClusterPackageService.IsHash(hash, 64) || hash != hash!.ToLowerInvariant())
            throw new InvalidDataException("A lowercase dependency manifest SHA-256 is required.");
    }

    private static byte[] Serialize(ClusterDependencyManifest manifest) => JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static ClusterDependencyCandidate Candidate(long revision, ClusterDependencyManifest manifest) =>
        new(revision, Hash(Serialize(manifest)), manifest.PackageVersion, manifest.DirectTransportCommit,
            manifest.Files.Count, manifest.Files.Values.Sum(file => file.Bytes));
}

public sealed record ClusterDependencyCandidate(long PackageSelectionRevision, string ManifestSha256,
    string PackageVersion, string DirectTransportCommit, int FileCount, long Bytes);
public sealed record ClusterDependencyRequest(string ManifestSha256, long? ExpectedPackageRevision, string? ExpectedDependencySha256);
public sealed record ClusterDependencyStatus(long PackageSelectionRevision, string? ManifestSha256,
    bool Verified, string? ErrorCode, ClusterDependencyCandidate? Installation);
public sealed record ClusterDependencyFile(string Sha256, long Bytes, bool Executable);
internal sealed record ClusterDependencyManifest(int SchemaVersion, string PackageVersion, string PackageSha256,
    string PackageCommit, string DirectTransportCommit, Dictionary<string, ClusterDependencyFile> Files);
