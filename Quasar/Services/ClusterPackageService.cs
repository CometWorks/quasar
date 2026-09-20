using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Magnetar.Protocol.Runtime;

namespace Quasar.Services;

/// <summary>Stages verified release files. It does not activate a deployment or launch processes.</summary>
public sealed class ClusterPackageService
{
    private const string RepositoryApi = "https://api.github.com/repos/CometWorks/cluster";
    private const long MaxArchiveBytes = 512L * 1024 * 1024;
    private const long MaxExtractedBytes = 2L * 1024 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    internal static readonly string[] RequiredFiles =
    [
        "manifest.json", "README.md", "gateway/ClusterGateway.dll",
        "gateway/ClusterGateway.runtimeconfig.json", "gateway/ClusterGateway.deps.json",
        "cli/cluster", "cli/cluster.py", "cli/magnetar_config.py", "cli/gateway-admin",
        "cli/ClusterGateway.Cli/ClusterGateway.Cli.dll", "tools/MagnetarWorld/MagnetarWorld.dll",
        "cli/ClusterGateway.Cli/ClusterGateway.Cli.runtimeconfig.json", "cli/ClusterGateway.Cli/ClusterGateway.Cli.deps.json",
        "tools/MagnetarWorld/MagnetarWorld.runtimeconfig.json", "tools/MagnetarWorld/MagnetarWorld.deps.json",
        "plugins/ClusterNode/ClusterNode.dll", "plugins/ClusterNode/ClusterNode.xml",
        "plugins/WorldAuthority/WorldAuthority.dll", "plugins/WorldAuthority/WorldAuthority.xml",
    ];
    private readonly IHttpClientFactory _clients;
    private readonly Func<string> _token;
    private readonly string _directory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ClusterPackageService(IHttpClientFactory clients, GitHubUpdateCredentialsCatalog credentials)
        : this(clients, () => credentials.GetCredentials().Token,
            Path.Combine(MagnetarPaths.GetQuasarManagedRuntimeToolsDirectory(), "Cluster")) { }

    internal ClusterPackageService(IHttpClientFactory clients, Func<string> token, string directory)
        => (_clients, _token, _directory) = (clients, token, directory);

    public async Task<ClusterPackageRelease> GetReleaseAsync(string? version, CancellationToken token)
    {
        if (version is not null) ValidateVersion(version);
        using var client = CreateClient();
        using var response = await client.GetAsync(RepositoryApi + "/releases/"
            + (version is null ? "latest" : "tags/v" + version), token);
        response.EnsureSuccessStatusCode();
        await response.Content.LoadIntoBufferAsync(1024 * 1024, token);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        var release = document.RootElement;
        string tag = release.GetProperty("tag_name").GetString() ?? "";
        if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean()
            || !tag.StartsWith('v'))
            throw new InvalidDataException("Only published stable cluster releases can be staged.");
        string resolved = tag[1..];
        ValidateVersion(resolved);
        if (version is not null && version != resolved)
            throw new InvalidDataException("Release tag does not match the requested version.");
        var assets = release.GetProperty("assets").EnumerateArray().ToArray();
        JsonElement Asset(string name)
        {
            var matches = assets.Where(a => a.GetProperty("name").GetString() == name).ToArray();
            return matches.Length == 1 ? matches[0]
                : throw new InvalidDataException($"Release must contain exactly one {name} asset.");
        }
        string archiveName = $"ClusterForLinux-{resolved}.tar.gz";
        var archive = Asset(archiveName);
        long size = archive.GetProperty("size").GetInt64();
        if (size <= 0 || size > MaxArchiveBytes)
            throw new InvalidDataException("Cluster archive exceeds the supported size limit.");
        using var sums = await DownloadAssetAsync(client, Asset("SHA256SUMS").GetProperty("id").GetInt64(), token);
        await sums.Content.LoadIntoBufferAsync(64 * 1024, token);
        string checksums = await sums.Content.ReadAsStringAsync(token);
        var hashes = checksums.Split('\n').Select(line => line.Trim().Split((char[]?)null, 2,
                StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length == 2 && parts[1].TrimStart('*') == archiveName).ToArray();
        if (hashes.Length != 1 || !IsHash(hashes[0][0], 64))
            throw new InvalidDataException("SHA256SUMS must identify the selected archive exactly once.");
        return new(resolved, release.GetProperty("id").GetInt64(), archive.GetProperty("id").GetInt64(),
            size, hashes[0][0].ToLowerInvariant());
    }

    public async Task<ClusterPackageInstallation> StageAsync(ClusterPackageRequest request, CancellationToken token)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Cluster release staging requires Linux.");
        ValidateVersion(request.Version);
        if (!IsHash(request.Sha256, 64))
            throw new InvalidDataException("The selected release archive SHA-256 is required.");
        await _gate.WaitAsync(token);
        string? staging = null;
        try
        {
            var release = await GetReleaseAsync(request.Version, token);
            if (!release.Sha256.Equals(request.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Release checksum changed; select the release again.");
            Directory.CreateDirectory(_directory);
            string destination = Path.Combine(_directory, release.Version);
            if (Directory.Exists(destination))
            {
                var installed = await ReadInstallationAsync(request, token);
                if (installed.Release != release)
                    throw new InvalidDataException("This version is already staged from different release assets.");
                return installed;
            }

            staging = Path.Combine(_directory, ".stage-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            string archivePath = Path.Combine(staging, "archive.tar.gz");
            using (var client = CreateClient())
            using (var response = await DownloadAssetAsync(client, release.ArchiveAssetId, token))
            await using (var source = await response.Content.ReadAsStreamAsync(token))
            await using (var target = File.Create(archivePath))
            {
                byte[] buffer = new byte[81920];
                long received = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, token)) != 0)
                {
                    received += read;
                    if (received > release.ArchiveBytes)
                        throw new InvalidDataException("Archive exceeds its advertised size.");
                    await target.WriteAsync(buffer.AsMemory(0, read), token);
                }
                if (received != release.ArchiveBytes)
                    throw new InvalidDataException("Archive download is incomplete.");
            }
            if (await HashAsync(archivePath, token) != release.Sha256)
                throw new InvalidDataException("Cluster archive failed SHA-256 verification.");
            await ExtractAsync(archivePath, staging, release.Version, token);
            File.Delete(archivePath);
            string package = Path.Combine(staging, "cluster-" + release.Version);
            foreach (string required in RequiredFiles)
                if (!File.Exists(Path.Combine(package, required)))
                    throw new InvalidDataException($"Cluster package is missing {required}.");
            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(package, "manifest.json"), token));
            var root = manifest.RootElement;
            string commit = root.GetProperty("commit").GetString() ?? "";
            if (root.GetProperty("name").GetString() != "cluster"
                || root.GetProperty("version").GetString() != release.Version || !IsHash(commit, 40))
                throw new InvalidDataException("Cluster manifest identity is invalid.");
            var files = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string path in Directory.EnumerateFiles(package, "*", SearchOption.AllDirectories))
                files.Add(Path.GetRelativePath(package, path), await HashAsync(path, token));
            var result = new ClusterPackageInstallation(release, commit,
                Path.Combine(destination, "cluster-" + release.Version), files);
            await File.WriteAllTextAsync(Path.Combine(staging, "installation.json"),
                JsonSerializer.Serialize(result, JsonOptions), token);
            // Promotion is a same-filesystem rename; an interrupted extraction is never an installation.
            Directory.Move(staging, destination);
            staging = null;
            return result;
        }
        finally
        {
            try { if (staging is not null) Directory.Delete(staging, recursive: true); }
            finally { _gate.Release(); }
        }
    }

    /// <summary>Verifies staged files locally without GitHub access or package activation.</summary>
    public async Task<ClusterPackageInstallation> GetInstalledAsync(ClusterPackageRequest request, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try { return await ReadInstallationAsync(request, token); }
        finally { _gate.Release(); }
    }

    private async Task<ClusterPackageInstallation> ReadInstallationAsync(ClusterPackageRequest request, CancellationToken token)
    {
        ValidateVersion(request.Version);
        if (!IsHash(request.Sha256, 64)) throw new InvalidDataException("The selected archive SHA-256 is required.");
        string directory = Path.Combine(_directory, request.Version);
        string receipt = Path.Combine(directory, "installation.json");
        // Refuse a redirected receipt before opening it, not only during the later tree walk.
        foreach (string path in new[] { _directory, directory, receipt })
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Staged package contains a link.");
        var installed = JsonSerializer.Deserialize<ClusterPackageInstallation>(
            await File.ReadAllTextAsync(receipt, token), JsonOptions)
            ?? throw new InvalidDataException("Package installation receipt is empty.");
        if (installed.Release is null || installed.Release.Version != request.Version
            || !string.Equals(installed.Release.Sha256, request.Sha256, StringComparison.OrdinalIgnoreCase)
            || !IsHash(installed.Commit, 40) || installed.Release.ReleaseId <= 0
            || installed.Release.ArchiveAssetId <= 0 || installed.Release.ArchiveBytes is <= 0 or > MaxArchiveBytes)
            throw new InvalidDataException("Package installation identity does not match the selection.");
        await VerifyInstallationAsync(directory, installed, token);
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(installed.PackagePath, "manifest.json"), token));
        var root = manifest.RootElement;
        if (root.GetProperty("name").GetString() != "cluster"
            || root.GetProperty("version").GetString() != request.Version
            || root.GetProperty("commit").GetString() != installed.Commit)
            throw new InvalidDataException("Package manifest does not match its installation receipt.");
        return installed;
    }

    internal static async Task ExtractAsync(string archive, string destination, string version, CancellationToken token)
    {
        using var gzip = new GZipStream(File.OpenRead(archive), CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        long total = 0;
        TarEntry? entry;
        while ((entry = await reader.GetNextEntryAsync(cancellationToken: token)) is not null)
        {
            string name = entry.Name.TrimEnd('/');
            string[] parts = name.Split('/');
            if (parts[0] != "cluster-" + version || parts.Any(p => p is "" or "." or "..")
                || name.Contains('\\') || !seen.Add(name) || seen.Count > 20000)
                throw new InvalidDataException("Cluster archive contains an unsafe or duplicate path.");
            string path = Path.Combine(destination, name);
            if (entry.EntryType == TarEntryType.Directory)
                Directory.CreateDirectory(path);
            else if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile)
            {
                if (entry.Length > MaxExtractedBytes - total)
                    throw new InvalidDataException("Cluster archive exceeds the extracted size limit.");
                total += entry.Length;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await entry.ExtractToFileAsync(path, overwrite: false, cancellationToken: token);
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite
                        | UnixFileMode.GroupRead | UnixFileMode.OtherRead
                        | (entry.Mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)));
            }
            else throw new InvalidDataException("Cluster archive links and special files are not supported.");
        }
    }

    private static async Task VerifyInstallationAsync(string directory, ClusterPackageInstallation installation, CancellationToken token)
    {
        string package = Path.Combine(directory, "cluster-" + installation.Release.Version);
        if (installation.PackagePath != package || installation.Files is null
            || RequiredFiles.Any(file => !installation.Files.ContainsKey(file)))
            throw new InvalidDataException("Package installation receipt is invalid.");
        // Refuse links before traversing the staged tree, including links introduced after installation.
        var pending = new Stack<string>();
        pending.Push(directory);
        var files = new HashSet<string>(StringComparer.Ordinal);
        while (pending.TryPop(out string? current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Staged package contains a link.");
            if (Directory.Exists(current))
                foreach (string child in Directory.EnumerateFileSystemEntries(current)) pending.Push(child);
            else if (current != Path.Combine(directory, "installation.json"))
            {
                string relative = Path.GetRelativePath(package, current);
                if (!installation.Files.TryGetValue(relative, out string? hash) || await HashAsync(current, token) != hash)
                    throw new InvalidDataException("Staged package contents changed.");
                files.Add(relative);
            }
        }
        if (!files.SetEquals(installation.Files.Keys))
            throw new InvalidDataException("Staged package files are missing.");
    }

    private HttpClient CreateClient()
    {
        var client = _clients.CreateClient(string.Empty); // Existing GitHub retry handler and encrypted credentials.
        client.Timeout = TimeSpan.FromMinutes(10);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Quasar");
        string credential = _token();
        if (!string.IsNullOrWhiteSpace(credential))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        return client;
    }

    private static async Task<HttpResponseMessage> DownloadAssetAsync(HttpClient client, long id, CancellationToken token)
    {
        if (id <= 0) throw new InvalidDataException("Release asset ID is invalid.");
        using var request = new HttpRequestMessage(HttpMethod.Get, RepositoryApi + "/releases/assets/" + id);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.IsSuccessStatusCode) return response;
        try { response.EnsureSuccessStatusCode(); return response; }
        catch { response.Dispose(); throw; }
    }

    internal static void ValidateVersion(string? version)
    {
        if (version is null || version.Length > 40 || !Regex.IsMatch(version, @"\A[0-9]+\.[0-9]+\.[0-9]+\z"))
            throw new InvalidDataException("A stable cluster version such as 1.0.3 is required.");
    }

    internal static bool IsHash(string? value, int length) => value?.Length == length && value.All(Uri.IsHexDigit);
    private static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).ToLowerInvariant();
    }
}

public sealed record ClusterPackageRelease(string Version, long ReleaseId, long ArchiveAssetId, long ArchiveBytes, string Sha256);
public sealed record ClusterPackageRequest(string Version, string Sha256);
public sealed record ClusterPackageSelectionRequest(string Version, string Sha256, long? ExpectedRevision);
public sealed record ClusterPackageSelectionStatus(long Revision, Quasar.Models.ClusterPackageSelection? Selection,
    bool Verified, string? ErrorCode);
public sealed record ClusterPackageInstallation(ClusterPackageRelease Release, string Commit, string PackagePath,
    Dictionary<string, string> Files);
