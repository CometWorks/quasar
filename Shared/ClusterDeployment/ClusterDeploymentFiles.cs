using System.Security.Cryptography;
using System.Text.Json;
using Quasar.Host.Contract.V1;

namespace Quasar.ClusterDeployment;

internal static partial class ClusterDeploymentFiles
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    internal static async Task<Dictionary<string, DeploymentFile>> InspectAsync(string root, CancellationToken token)
    {
        var files = new SortedDictionary<string, DeploymentFile>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(root));
        long bytes = 0;
        while (pending.TryPop(out string? path))
        {
            token.ThrowIfCancellationRequested();
            RefuseLinks(path);
            if (Directory.Exists(path))
            {
                foreach (string child in Directory.EnumerateFileSystemEntries(path)) pending.Push(child);
                continue;
            }
            string relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            ValidateRelative(relative);
            long size = new FileInfo(path).Length;
            if (size > 100L * 1024 * 1024 * 1024 - bytes || files.Count >= 200_000)
                throw new InvalidDataException("Deployment exceeds supported limits.");
            bytes += size;
            await using var stream = File.OpenRead(path);
            string hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).ToLowerInvariant();
            if (stream.Length != size) throw new InvalidDataException("Deployment input changed.");
            files.Add(relative, new(hash, size, !OperatingSystem.IsWindows()
                && (File.GetUnixFileMode(path) & UnixFileMode.UserExecute) != 0));
        }
        return new(files, StringComparer.Ordinal);
    }

    private static ClusterDeploymentInputs ReadInputs(byte[] bytes, string expectedHash)
    {
        if (Hash(bytes) != expectedHash) throw new InvalidDataException("Deployment inputs failed SHA-256 verification.");
        var inputs = JsonSerializer.Deserialize<ClusterDeploymentInputs>(bytes, JsonOptions)
            ?? throw new InvalidDataException("Deployment inputs are empty.");
        if (inputs.PackageSelectionRevision <= 0 || string.IsNullOrWhiteSpace(inputs.ClusterId)
            || inputs.Files is null || inputs.Files.Count == 0 || inputs.Files.Count > 200_000
            || !Path.IsPathFullyQualified(inputs.PackageDirectory) || !Path.IsPathFullyQualified(inputs.DependencyDirectory))
            throw new InvalidDataException("Deployment inputs are invalid.");
        if (Path.GetFullPath(inputs.PackageDirectory) != inputs.PackageDirectory
            || Path.GetFullPath(inputs.DependencyDirectory) != inputs.DependencyDirectory)
            throw new InvalidDataException("Deployment input directories must be canonical absolute paths.");
        foreach (var (path, pin) in inputs.Files)
        {
            ValidateRelative(path);
            if ((!path.StartsWith("Package/", StringComparison.Ordinal) && !path.StartsWith("Dependencies/", StringComparison.Ordinal))
                || pin is null || pin.Bytes < 0 || pin.Sha256?.Length != 64 || !pin.Sha256.All(Uri.IsHexDigit))
                throw new InvalidDataException("Deployment file pin is invalid.");
        }
        if (inputs.Files.Values.Sum(p => (decimal)p.Bytes) > 100m * 1024 * 1024 * 1024)
            throw new InvalidDataException("Deployment exceeds supported limits.");
        return inputs;
    }

    internal static async Task<PreparedClusterDeployment> PrepareAsync(byte[] bytes, string expectedHash,
        string directory, CancellationToken token)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Cluster deployment requires Linux.");
        var inputs = ReadInputs(bytes, expectedHash);
        directory = Path.GetFullPath(directory);
        string destination = Path.Combine(directory, expectedHash);
        var environment = EnvironmentFor(destination);
        byte[] environmentBytes = JsonSerializer.SerializeToUtf8Bytes(environment, JsonOptions);
        if (Directory.Exists(destination))
        {
            RefuseLinks(destination);
            string[] expectedEntries = ["Package", "Dependencies", "inputs.json", "launch-environment.json"];
            if (!Directory.EnumerateFileSystemEntries(destination).Select(Path.GetFileName).ToHashSet()
                    .SetEquals(expectedEntries))
                throw new InvalidDataException("Prepared deployment contains unexpected entries.");
            if (Hash(await File.ReadAllBytesAsync(Resolve(destination, "inputs.json"), token)) != expectedHash
                || !(await File.ReadAllBytesAsync(Resolve(destination, "launch-environment.json"), token)).SequenceEqual(environmentBytes))
                throw new InvalidDataException("Prepared deployment metadata changed.");
            await VerifyAsync(inputs, Path.Combine(destination, "Package"), Path.Combine(destination, "Dependencies"), token);
            return new(destination, expectedHash, Path.Combine(destination, "launch-environment.json"));
        }
        if (directory == inputs.PackageDirectory || directory.StartsWith(inputs.PackageDirectory + "/", StringComparison.Ordinal)
            || directory == inputs.DependencyDirectory || directory.StartsWith(inputs.DependencyDirectory + "/", StringComparison.Ordinal))
            throw new InvalidDataException("Deployment output cannot be inside an input tree.");
        await VerifyAsync(inputs, inputs.PackageDirectory, inputs.DependencyDirectory, token);
        Directory.CreateDirectory(directory);
        RefuseLinks(directory);
        string staging = Path.Combine(directory, ".stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var (path, pin) in inputs.Files)
            {
                token.ThrowIfCancellationRequested();
                bool package = path.StartsWith("Package/", StringComparison.Ordinal);
                string source = Resolve(package ? inputs.PackageDirectory : inputs.DependencyDirectory,
                    path[(package ? "Package/".Length : "Dependencies/".Length)..]);
                string target = Path.Combine(staging, path);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using var input = File.OpenRead(source);
                await using var output = File.Create(target);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                byte[] buffer = new byte[81920];
                long copied = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, token)) != 0)
                {
                    copied += read;
                    if (copied > pin.Bytes) throw new InvalidDataException("Deployment source changed during copy.");
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                }
                if (copied != pin.Bytes || Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() != pin.Sha256)
                    throw new InvalidDataException("Deployment source changed during copy.");
                File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite
                    | (pin.Executable ? UnixFileMode.UserExecute : 0));
            }
            await File.WriteAllBytesAsync(Path.Combine(staging, "inputs.json"), bytes, token);
            await File.WriteAllBytesAsync(Path.Combine(staging, "launch-environment.json"), environmentBytes, token);
            token.ThrowIfCancellationRequested();
            Directory.Move(staging, destination); // Atomic and no overwrite, including concurrent preparation.
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
        return new(destination, expectedHash, Path.Combine(destination, "launch-environment.json"));
    }

    private static async Task VerifyAsync(ClusterDeploymentInputs inputs, string package, string dependencies, CancellationToken token)
    {
        var actual = (await InspectAsync(package, token)).ToDictionary(x => "Package/" + x.Key, x => x.Value);
        foreach (var (path, pin) in await InspectAsync(dependencies, token)) actual.Add("Dependencies/" + path, pin);
        if (actual.Count != inputs.Files.Count || actual.Any(x => !inputs.Files.TryGetValue(x.Key, out var pin) || pin != x.Value))
            throw new InvalidDataException("Deployment files do not match approved inputs.");
        if (!actual.TryGetValue("Dependencies/manifest.json", out var manifestPin) || manifestPin.Sha256 != inputs.DependencyManifestSha256)
            throw new InvalidDataException("Dependency manifest pin does not match deployment inputs.");
        if (actual.Keys.Any(path => path.StartsWith("Dependencies/", StringComparison.Ordinal)
                && path != "Dependencies/manifest.json" && !path.StartsWith("Dependencies/payload/", StringComparison.Ordinal)))
            throw new InvalidDataException("Dependency snapshot contains an unexpected entry.");
        using var manifest = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(dependencies, "manifest.json"), token));
        var data = manifest.RootElement;
        if (data.GetProperty("schemaVersion").GetInt32() != 2
            || data.GetProperty("packageVersion").GetString() != inputs.PackageVersion
            || data.GetProperty("packageSha256").GetString() != inputs.PackageSha256
            || data.GetProperty("packageCommit").GetString() != inputs.PackageCommit)
            throw new InvalidDataException("Package and dependencies are incompatible.");
        var payloadPins = data.GetProperty("files").Deserialize<Dictionary<string, DeploymentFile>>(JsonOptions)
            ?? throw new InvalidDataException("Dependency payload pins are missing.");
        var payload = actual.Where(x => x.Key.StartsWith("Dependencies/payload/", StringComparison.Ordinal))
            .ToDictionary(x => x.Key["Dependencies/payload/".Length..], x => x.Value);
        if (payload.Count != payloadPins.Count || payload.Any(x => !payloadPins.TryGetValue(x.Key, out var pin) || pin != x.Value))
            throw new InvalidDataException("Dependency payload does not match its pinned manifest.");
        foreach (string required in new[] { "DedicatedServer/DedicatedServer64/SpaceEngineersDedicated.exe",
                     "Magnetar/MagnetarInterim.bin", "Magnetar/Libraries/MagnetarInterim/PluginSdk.dll",
                     "Magnetar/Libraries/MagnetarInterim/libsteam_api.so",
                     "DirectTransport/DirectTransport.dll", "DirectTransport/DirectTransport.xml", "DirectTransport/LiteNetLib.dll" })
            if (!payload.TryGetValue(required, out var file) || file.Bytes == 0)
                throw new InvalidDataException($"Dependency snapshot is missing {required}.");
        if (!payload["Magnetar/MagnetarInterim.bin"].Executable)
            throw new InvalidDataException("Magnetar launcher is not executable.");
        using var release = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(package, "manifest.json"), token));
        if (release.RootElement.GetProperty("version").GetString() != inputs.PackageVersion
            || release.RootElement.GetProperty("commit").GetString() != inputs.PackageCommit)
            throw new InvalidDataException("Package identity does not match deployment inputs.");
        ClusterPluginBundles.Validate(Path.Combine(dependencies, "payload/CommonPlugins"));
        ValidateCapabilities(package);
        string? sdkHash = GetPinnedPluginSdkSha256(package);
        if (sdkHash is not null && sdkHash != payload["Magnetar/Libraries/MagnetarInterim/PluginSdk.dll"].Sha256)
            throw new InvalidDataException("Magnetar PluginSdk does not match the SDK used to build this cluster release.");
    }

    internal static string? GetPinnedPluginSdkSha256(string package)
    {
        using var capability = JsonDocument.Parse(File.ReadAllBytes(Resolve(package, "cli/deployment-capabilities.json")));
        if (!capability.RootElement.TryGetProperty("pluginServices", out var services) || services.GetInt32() != 1)
            return null;
        if (!capability.RootElement.TryGetProperty("pluginSdkSha256", out var pin) || pin.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Cluster release has an invalid PluginSdk SHA-256 pin.");
        string? hash = pin.GetString();
        if (hash is null || hash.Length != 64 || hash != hash.ToLowerInvariant() || !hash.All(Uri.IsHexDigit))
            throw new InvalidDataException("Cluster release has an invalid PluginSdk SHA-256 pin.");
        return hash;
    }

    internal static void ValidateCapabilities(string package)
    {
        string path = Resolve(package, "cli/deployment-capabilities.json");
        using var capabilities = JsonDocument.Parse(File.ReadAllBytes(path));
        if (capabilities.RootElement.GetProperty("schemaVersion").GetInt32() != 1
            || !capabilities.RootElement.GetProperty("frozenPluginBundles").GetBoolean())
            throw new InvalidDataException("Cluster package does not support frozen plugin bundles.");
    }

    private static Dictionary<string, string> EnvironmentFor(string root) => new()
    {
        ["SPACE_ENGINEERS_BIN64"] = Path.Combine(root, "Dependencies/payload/DedicatedServer/DedicatedServer64"),
        ["MAGNETAR_HOME"] = Path.Combine(root, "Dependencies/payload/Magnetar"),
        ["DIRECT_TRANSPORT_BINARIES"] = Path.Combine(root, "Dependencies/payload/DirectTransport"),
        ["CLUSTER_COMMON_PLUGINS"] = Path.Combine(root, "Dependencies/payload/CommonPlugins"),
        ["CLUSTER_PLUGIN_BINARIES"] = Path.Combine(root, "Package/plugins"),
        ["MAGNETAR_GATEWAY_DLL"] = Path.Combine(root, "Package/gateway/ClusterGateway.dll"),
        ["MAGNETAR_WORLD_TOOL"] = Path.Combine(root, "Package/tools/MagnetarWorld/MagnetarWorld.dll"),
        ["CLUSTER_PLUGIN_SOURCE"] = "", ["DIRECT_TRANSPORT_SOURCE"] = "",
        ["CLUSTER_EXTRA_PLUGINS"] = "", ["CLUSTER_HUB_PLUGINS"] = "",
    };

    private static void ValidateRelative(string path)
    {
        if (Path.IsPathRooted(path) || path.Contains('\\') || path.Split('/').Any(x => x is "" or "." or ".."))
            throw new InvalidDataException("Deployment paths must be relative and stay inside their root.");
    }
    private static string Resolve(string root, string relative)
    {
        ValidateRelative(relative);
        string path = Path.Combine(root, relative);
        RefuseLinks(path);
        return path;
    }
    private static void RefuseLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Deployment paths must not contain symbolic links.");
    }
}
