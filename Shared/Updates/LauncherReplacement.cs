using Magnetar.Protocol.Runtime;

namespace Quasar.Updates;

// Native startup wrappers recover this transaction even if the new executable
// cannot load. Never replace those wrappers from a running update: their upgrade
// belongs to the installer. Game data and the packaged fallback UI are excluded.
internal static class LauncherReplacement
{
    internal const string ManifestName = "launcher-files.txt";
    internal static string TransactionDirectory(string root) => Path.Combine(root, "Updates", "LauncherTransaction");
    internal static string Phase(string root)
    {
        var path = Path.Combine(TransactionDirectory(root), "phase");
        return File.Exists(path) ? QuasarUpdateJournal.ReadText(path).Trim() : "";
    }
    internal static void SetPhase(string root, string phase) => QuasarUpdateJournal.WriteText(Path.Combine(TransactionDirectory(root), "phase"), phase + "\n");

    internal static string[] ReadManifest(string directory)
    {
        var path = Path.Combine(directory, ManifestName);
        if (!File.Exists(path)) throw new InvalidDataException($"Missing {path}. Reinstall the complete Quasar package before updating.");
        var names = File.ReadAllLines(path).Append(ManifestName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var name in names) ValidateName(name);
        return names;
    }

    internal static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains('\t') || name.Contains('\r') || name.Contains('\n') || name.Contains('\\') || name.Contains(':') ||
            Path.IsPathRooted(name) || name.Split('/').Any(part => part is ".." or "." or ""))
            throw new InvalidDataException($"Invalid launcher manifest entry: {name}");
        if (name.Equals("appsettings.json", StringComparison.OrdinalIgnoreCase) || name.StartsWith("WebService/", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Updater/", StringComparison.OrdinalIgnoreCase) || name.Equals(QuasarServiceInstallation.FileName, StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Updates/", StringComparison.OrdinalIgnoreCase) || name.StartsWith("ManagedRuntime/", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("quasar-supervisor.sh", StringComparison.OrdinalIgnoreCase) || name.Equals("quasar-supervisor.ps1", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Launcher manifest includes protected path: {name}");
    }

    internal static void Apply(string source, string root)
    {
        root = Path.GetFullPath(root);
        if (Phase(root) is "installing" or "ready" or "attempted")
            throw new InvalidOperationException("A launcher replacement still needs recovery or health confirmation. Restart Bootstrap before retrying.");
        var incoming = ReadManifest(source);
        var old = ReadManifest(root);
        foreach (var name in incoming)
        {
            var path = SafePath(source, name);
            if (!File.Exists(path)) throw new InvalidDataException($"Incomplete launcher package: {name}");
        }
        var transaction = TransactionDirectory(root);
        // Retain the last complete snapshot until its replacement is proven healthy.
        // Only a committed/rolled-back transaction may be replaced by a new attempt.
        if (Directory.Exists(transaction)) Directory.Delete(transaction, recursive: true);
        Directory.CreateDirectory(Path.Combine(transaction, "previous"));
        var files = old.Concat(incoming).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var entries = new List<string>();
        foreach (var name in files)
        {
            var path = SafePath(root, name);
            entries.Add((File.Exists(path) ? "+" : "-") + name);
            if (File.Exists(path)) CopyFile(path, SafePath(Path.Combine(transaction, "previous"), name));
        }
        // Recovery pointers are part of the same transaction, including absence.
        foreach (var name in new[] { "Updates/active-release.json", "Updates/active-release.json.previous", "appsettings.json" })
        {
            var path = SafePath(root, name);
            entries.Add((File.Exists(path) ? "+" : "-") + name);
            if (File.Exists(path)) CopyFile(path, SafePath(Path.Combine(transaction, "previous"), name));
        }
        QuasarUpdateJournal.WriteText(Path.Combine(transaction, "files"), string.Join('\n', entries) + "\n");
        UpdateDurability.SyncTree(transaction);
        SetPhase(root, "installing");
        try
        {
            foreach (var name in incoming) ReplaceFile(SafePath(source, name), SafePath(root, name), transaction);
            foreach (var name in old.Except(incoming, StringComparer.OrdinalIgnoreCase))
                if (File.Exists(SafePath(root, name))) File.Move(SafePath(root, name), Path.Combine(transaction, Guid.NewGuid().ToString("N") + ".retired"));
            SetPhase(root, "ready");
        }
        catch
        {
            Restore(root); // If locks/permissions prevent recovery, leave phase for the native wrapper.
            throw;
        }
    }

    internal static void Restore(string root)
    {
        var transaction = TransactionDirectory(root);
        foreach (var entry in File.ReadAllLines(Path.Combine(transaction, "files")))
        {
            var name = entry[1..];
            var destination = SafePath(root, name);
            if (entry[0] == '+') ReplaceFile(SafePath(Path.Combine(transaction, "previous"), name), destination, transaction);
            else if (entry[0] == '-' && File.Exists(destination)) File.Move(destination, Path.Combine(transaction, Guid.NewGuid().ToString("N") + ".retired"));
            else if (entry[0] != '-') throw new InvalidDataException("Invalid launcher recovery manifest.");
        }
        SetPhase(root, "rolled-back");
    }

    internal static void BackupSettings(string root, string data)
    {
        var path = Path.Combine(data, "appsettings.json");
        var transaction = TransactionDirectory(root);
        if (File.Exists(path)) QuasarUpdateJournal.WriteText(Path.Combine(transaction, "data-appsettings.json"), File.ReadAllText(path));
        QuasarUpdateJournal.WriteText(Path.Combine(transaction, "data-settings-backed-up"), File.Exists(path) ? "present" : "absent");
    }
    internal static void RestoreSettings(string root, string data)
    {
        var transaction = TransactionDirectory(root);
        var marker = Path.Combine(transaction, "data-settings-backed-up");
        if (!File.Exists(marker)) return;
        var path = Path.Combine(data, "appsettings.json");
        if (File.ReadAllText(marker) == "present") QuasarUpdateJournal.WriteText(path, File.ReadAllText(Path.Combine(transaction, "data-appsettings.json")));
        else if (File.Exists(path)) File.Delete(path);
    }

    private static string SafePath(string root, string name)
    {
        var path = Path.GetFullPath(Path.Combine(root, name));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!path.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, comparison))
            throw new InvalidDataException("Launcher path escapes its root.");
        for (var parent = new FileInfo(path).Directory; parent is not null && parent.FullName.Length >= Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar).Length; parent = parent.Parent)
            if (parent.LinkTarget is not null) throw new InvalidDataException($"Launcher path follows a symbolic link: {path}");
        if (new FileInfo(path).LinkTarget is not null) throw new InvalidDataException($"Launcher file is a symbolic link: {path}");
        return path;
    }

    private static void ReplaceFile(string source, string destination, string transaction)
    {
        var temporary = destination + ".update-" + Guid.NewGuid().ToString("N");
        try
        {
            CopyFile(source, temporary);
            if (File.Exists(destination)) File.Move(destination, Path.Combine(transaction, Guid.NewGuid().ToString("N") + ".retired"));
            File.Move(temporary, destination);
            UpdateDurability.SyncDirectory(Path.GetDirectoryName(destination)!);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void CopyFile(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using (var input = File.OpenRead(source))
        using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
        { input.CopyTo(output); output.Flush(flushToDisk: true); }
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(destination, File.GetUnixFileMode(source));
    }
}
