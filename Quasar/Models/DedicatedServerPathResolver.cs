using Magnetar.Protocol.Runtime;

namespace Quasar.Models;

public static class DedicatedServerPathResolver
{
    public static ResolvedDedicatedServerPaths Resolve(
        DedicatedServerDefinition definition,
        string? quasarRoot = null)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var root = Path.GetFullPath(quasarRoot ?? MagnetarPaths.GetQuasarDirectory());
        var serverRoot = Path.Combine(root, "Magnetars", definition.UniqueName.Trim());
        var defaultDedicatedServerPath = Path.Combine(serverRoot, "DedicatedServer");
        var defaultMagnetarPath = Path.Combine(serverRoot, "Magnetar");
        var dedicatedServerPath = ResolvePath(definition.DedicatedServerAppDataPath, defaultDedicatedServerPath, root);
        var magnetarPath = ResolvePath(definition.MagnetarAppDataPath, defaultMagnetarPath, root);
        var savesPath = ResolvePath(definition.WorldPath, Path.Combine(dedicatedServerPath, "Saves"), root);
        var configPath = ResolvePath(
            definition.ConfigFilePath,
            Path.Combine(dedicatedServerPath, "SpaceEngineers-Dedicated.cfg"),
            root);
        var saveName = definition.WorldSaveName?.Trim() ?? string.Empty;

        return new ResolvedDedicatedServerPaths(
            root,
            serverRoot,
            ResolveOptionalPath(definition.ExecutablePath, root),
            ResolveOptionalPath(definition.WorkingDirectory, root),
            dedicatedServerPath,
            magnetarPath,
            savesPath,
            string.IsNullOrWhiteSpace(saveName) ? string.Empty : Path.Combine(savesPath, saveName),
            configPath);
    }

    public static void CanonicalizeForStorage(
        DedicatedServerDefinition definition,
        string? quasarRoot = null)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var root = Path.GetFullPath(quasarRoot ?? MagnetarPaths.GetQuasarDirectory());
        var resolved = Resolve(definition, root);
        var defaultDedicatedServerPath = Path.Combine(resolved.ServerRoot, "DedicatedServer");
        var defaultMagnetarPath = Path.Combine(resolved.ServerRoot, "Magnetar");

        definition.ExecutablePath = ToStoredPath(definition.ExecutablePath, null, root);
        definition.WorkingDirectory = ToStoredPath(definition.WorkingDirectory, null, root);
        definition.DedicatedServerAppDataPath = ToStoredPath(
            definition.DedicatedServerAppDataPath,
            defaultDedicatedServerPath,
            root);
        definition.MagnetarAppDataPath = ToStoredPath(
            definition.MagnetarAppDataPath,
            defaultMagnetarPath,
            root);
        definition.WorldPath = ToStoredPath(
            definition.WorldPath,
            Path.Combine(resolved.DedicatedServerAppDataPath, "Saves"),
            root);
        definition.ConfigFilePath = ToStoredPath(
            definition.ConfigFilePath,
            Path.Combine(resolved.DedicatedServerAppDataPath, "SpaceEngineers-Dedicated.cfg"),
            root);
    }

    public static string ResolvePath(string? value, string fallback, string? quasarRoot = null)
    {
        var root = Path.GetFullPath(quasarRoot ?? MagnetarPaths.GetQuasarDirectory());
        if (string.IsNullOrWhiteSpace(value))
            return Path.GetFullPath(fallback);

        var normalized = NormalizeSeparators(value.Trim());
        return Path.GetFullPath(Path.IsPathRooted(normalized)
            ? normalized
            : Path.Combine(root, normalized));
    }

    public static string ToStoredPath(string? value, string? fallback, string? quasarRoot = null)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var root = Path.GetFullPath(quasarRoot ?? MagnetarPaths.GetQuasarDirectory());
        var resolved = ResolvePath(value, fallback ?? root, root);
        if (fallback is not null && PathsEqual(resolved, Path.GetFullPath(fallback)))
            return string.Empty;

        if (!IsPathWithinRoot(resolved, root))
            return resolved;

        return ToPortablePath(Path.GetRelativePath(root, resolved));
    }

    public static bool IsAbsoluteOutsideQuasarRoot(string? value, string? quasarRoot = null)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var normalized = NormalizeSeparators(value.Trim());
        if (!Path.IsPathRooted(normalized))
            return false;

        var root = Path.GetFullPath(quasarRoot ?? MagnetarPaths.GetQuasarDirectory());
        return !IsPathWithinRoot(Path.GetFullPath(normalized), root);
    }

    public static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;

        return string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    public static bool IsPathWithinRoot(string path, string root)
    {
        var fullPath = Path.GetFullPath(path);
        var fullRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (PathsEqual(fullPath, fullRoot))
            return true;

        return fullPath.StartsWith(
            fullRoot + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static string ResolveOptionalPath(string? value, string root) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : ResolvePath(value, root, root);

    private static string NormalizeSeparators(string value) =>
        value.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);

    private static string ToPortablePath(string value) =>
        value.Replace('\\', '/');
}

public sealed record ResolvedDedicatedServerPaths(
    string QuasarRoot,
    string ServerRoot,
    string ExecutablePath,
    string WorkingDirectory,
    string DedicatedServerAppDataPath,
    string MagnetarAppDataPath,
    string SavesPath,
    string WorldSavePath,
    string ConfigFilePath);
