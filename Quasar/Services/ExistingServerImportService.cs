using System.Globalization;
using System.Xml.Linq;
using Magnetar.Protocol.Runtime;
using Quasar.Models;

namespace Quasar.Services;

public enum ExistingServerKind
{
    Vanilla,
    Torch,
}

public enum ExistingServerTransferMode
{
    Copy,
    Move,
}

[Flags]
public enum ExistingServerImportSections
{
    None = 0,
    Identity = 1 << 0,
    Network = 1 << 1,
    ServerSettings = 1 << 2,
    AccessLists = 1 << 3,
    SessionSettings = 1 << 4,
    Mods = 1 << 5,
    TorchLifecycle = 1 << 6,
}

public sealed class ExistingServerWorldCandidate
{
    public required string Name { get; init; }

    public required string Path { get; init; }

    public bool IsConfiguredWorld { get; init; }

    public int SessionSettingCount { get; init; }

    public int ModCount { get; init; }

    internal QuasarConfigProfile ImportedProfile { get; init; } = new();

    public QuasarConfigProfile DetectedProfile => ImportedProfile;
}

public sealed class ExistingServerImportAnalysis
{
    public required ExistingServerKind Kind { get; init; }

    public required string SourcePath { get; init; }

    public required string AppDataPath { get; init; }

    public required string ConfigPath { get; init; }

    public required string ServerName { get; init; }

    public required string WorldName { get; init; }

    public required string ServerIp { get; init; }

    public int ServerPort { get; init; }

    public int RootSettingCount { get; init; }

    public int SessionSettingCount { get; init; }

    public int AccessEntryCount { get; init; }

    public bool HasPasswordHash { get; init; }

    public bool TorchAutostart { get; init; }

    public bool TorchRestartOnCrash { get; init; }

    public int TorchPluginCount { get; init; }

    public bool TorchWhitelistEnabled { get; init; }

    public int TorchWhitelistEntryCount { get; init; }

    public required IReadOnlyList<ExistingServerWorldCandidate> Worlds { get; init; }

    public required IReadOnlyList<string> Warnings { get; init; }

    internal QuasarConfigProfile ImportedProfile { get; init; } = new();

    public QuasarConfigProfile DetectedProfile => ImportedProfile;
}

public sealed record ExistingServerImportRequest(
    ExistingServerKind Kind,
    string SourcePath,
    ExistingServerTransferMode TransferMode,
    string DisplayName,
    string UniqueName,
    IReadOnlyList<string> SelectedWorldPaths,
    string PrimaryWorldPath,
    ExistingServerImportSections Sections,
    bool SourceServerStoppedConfirmed);

public sealed record ExistingServerImportResult(
    DedicatedServerDefinition Server,
    QuasarConfigProfile Profile,
    IReadOnlyList<QuasarWorldTemplate> WorldTemplates,
    IReadOnlyList<string> Warnings);

public sealed class ExistingServerImportService(
    DedicatedServerCatalog servers,
    QuasarWorldTemplateCatalog worldTemplates,
    QuasarConfigProfileCatalog profiles,
    ILogger<ExistingServerImportService> logger)
{
    public ExistingServerImportAnalysis Analyze(ExistingServerKind kind, string sourcePath) =>
        ExistingServerImportAnalyzer.Analyze(kind, sourcePath);

    public async Task<ExistingServerImportResult> ImportAsync(
        ExistingServerImportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.SourceServerStoppedConfirmed)
            throw new InvalidOperationException("Confirm that the source server is stopped before importing it.");

        var analysis = Analyze(request.Kind, request.SourcePath);
        var selectedWorlds = ResolveSelectedWorlds(analysis, request.SelectedWorldPaths);
        var primaryWorld = selectedWorlds.FirstOrDefault(world => PathsEqual(world.Path, request.PrimaryWorldPath))
            ?? throw new InvalidOperationException("Primary world must be one of the selected worlds.");

        if (request.TransferMode == ExistingServerTransferMode.Move)
            ValidateMoveSources(analysis, selectedWorlds);

        var uniqueName = servers.GenerateUniqueServerName(request.UniqueName);
        var displayName = NormalizeName(request.DisplayName, analysis.ServerName, primaryWorld.Name, uniqueName);
        var importWarnings = new List<string>();
        var createdTemplates = new List<QuasarWorldTemplate>();
        QuasarConfigProfile? createdProfile = null;
        DedicatedServerDefinition? createdServer = null;
        string? createdServerRoot = null;

        try
        {
            QuasarWorldTemplate? primaryTemplate = null;
            foreach (var world in selectedWorlds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var template = await worldTemplates.ImportAsync(
                    world.Name,
                    $"Imported from {FormatKind(request.Kind)} server at '{analysis.SourcePath}'.",
                    world.Path,
                    cancellationToken);
                createdTemplates.Add(template);
                if (PathsEqual(world.Path, primaryWorld.Path))
                    primaryTemplate = template;
            }

            if (primaryTemplate is null)
                throw new InvalidOperationException("Primary world template was not created.");
            createdProfile = BuildProfile(analysis, primaryWorld, primaryTemplate, displayName, request.Sections);
            await profiles.UpsertAsync(createdProfile, cancellationToken);

            var preferredPort = request.Sections.HasFlag(ExistingServerImportSections.Network)
                ? analysis.ServerPort
                : 27016;
            var serverPort = AllocateAvailablePort(preferredPort);
            if (serverPort != preferredPort)
                importWarnings.Add($"Port {preferredPort} was already used by Quasar; imported server uses {serverPort}.");

            createdServer = new DedicatedServerDefinition
            {
                UniqueName = uniqueName,
                DisplayName = displayName,
                InGameServerName = request.Sections.HasFlag(ExistingServerImportSections.Identity)
                    ? analysis.ServerName
                    : string.Empty,
                InGameWorldName = request.Sections.HasFlag(ExistingServerImportSections.Identity)
                    ? NormalizeName(analysis.WorldName, primaryWorld.Name)
                    : string.Empty,
                WorldSaveName = uniqueName,
                ConfigProfileId = createdProfile.ConfigProfileId,
                WorldTemplateId = primaryTemplate.WorldTemplateId,
                ServerPort = serverPort,
                ServerIP = request.Sections.HasFlag(ExistingServerImportSections.Network)
                    ? NormalizeIp(analysis.ServerIp)
                    : "0.0.0.0",
                RestartOnCrash = request.Sections.HasFlag(ExistingServerImportSections.TorchLifecycle) &&
                                 analysis.Kind == ExistingServerKind.Torch
                    ? analysis.TorchRestartOnCrash
                    : true,
                GoalState = DedicatedServerGoalState.Off,
                AutoStart = false,
            };

            var paths = DedicatedServerPathResolver.Resolve(createdServer);
            createdServerRoot = paths.ServerRoot;
            await CopyWorldDirectoryAsync(
                worldTemplates.GetWorldDirectory(primaryTemplate.WorldTemplateId),
                paths.WorldSavePath,
                cancellationToken);
            await servers.UpsertAsync(createdServer, cancellationToken);
            createdServer = servers.GetServer(uniqueName) ?? createdServer;
        }
        catch
        {
            await RollBackAsync(createdServer, createdProfile, createdTemplates, createdServerRoot);
            throw;
        }

        if (request.TransferMode == ExistingServerTransferMode.Move)
        {
            foreach (var world in selectedWorlds)
            {
                try
                {
                    Directory.Delete(world.Path, recursive: true);
                    logger.LogInformation("Removed imported source world at {Path} after successful move.", world.Path);
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Imported source world could not be removed from {Path}.", world.Path);
                    importWarnings.Add($"Imported '{world.Name}', but source folder could not be removed: {exception.Message}");
                }
            }
        }

        logger.LogInformation(
            "Imported {Kind} server {UniqueName} from {SourcePath} with {WorldCount} world(s).",
            request.Kind,
            createdServer!.UniqueName,
            analysis.SourcePath,
            createdTemplates.Count);

        return new ExistingServerImportResult(createdServer!, createdProfile!, createdTemplates, importWarnings);
    }

    private static IReadOnlyList<ExistingServerWorldCandidate> ResolveSelectedWorlds(
        ExistingServerImportAnalysis analysis,
        IReadOnlyList<string> selectedPaths)
    {
        var selected = analysis.Worlds
            .Where(world => selectedPaths.Any(path => PathsEqual(path, world.Path)))
            .ToList();
        if (selected.Count == 0)
            throw new InvalidOperationException("Select at least one world to import.");

        if (selected.Count != selectedPaths.Distinct(PathComparer).Count())
            throw new InvalidOperationException("One or more selected worlds no longer belong to this source server.");

        return selected;
    }

    internal static QuasarConfigProfile BuildProfile(
        ExistingServerImportAnalysis analysis,
        ExistingServerWorldCandidate primaryWorld,
        QuasarWorldTemplate primaryTemplate,
        string displayName,
        ExistingServerImportSections sections)
    {
        var profile = new QuasarConfigProfile
        {
            Name = $"{displayName} Imported",
            Description = $"Imported from {FormatKind(analysis.Kind)} server at '{analysis.SourcePath}'.",
            SourceWorldTemplateId = primaryTemplate.WorldTemplateId,
        };

        if (sections.HasFlag(ExistingServerImportSections.ServerSettings))
        {
            CopyOptions(
                analysis.ImportedProfile,
                profile,
                QuasarConfigOptionScope.Root,
                option => option.PropertyName is not nameof(QuasarWorldRootSettings.CrossPlatform) and
                    not nameof(QuasarWorldRootSettings.NetworkType));
        }

        var worldProfile = primaryWorld.SessionSettingCount > 0
            ? primaryWorld.ImportedProfile
            : analysis.ImportedProfile;
        if (sections.HasFlag(ExistingServerImportSections.SessionSettings))
        {
            CopyOptions(
                worldProfile,
                profile,
                QuasarConfigOptionScope.Session,
                option => option.PropertyName != nameof(QuasarSessionSettings.OnlineMode));
        }

        if (sections.HasFlag(ExistingServerImportSections.Network))
        {
            CopyOptions(
                analysis.ImportedProfile,
                profile,
                QuasarConfigOptionScope.Root,
                option => option.PropertyName is nameof(QuasarWorldRootSettings.CrossPlatform) or
                    nameof(QuasarWorldRootSettings.NetworkType));
            profile.SessionSettings.OnlineMode = worldProfile.SessionSettings.OnlineMode;
        }

        if (sections.HasFlag(ExistingServerImportSections.AccessLists))
        {
            var source = analysis.ImportedProfile.RootSettings;
            profile.RootSettings.GroupId = source.GroupId;
            profile.RootSettings.Administrators = source.Administrators.ToList();
            profile.RootSettings.Reserved = source.Reserved.ToList();
            profile.RootSettings.Banned = source.Banned.ToList();
        }

        if (sections.HasFlag(ExistingServerImportSections.Mods))
        {
            profile.Mods = primaryWorld.ImportedProfile.Mods
                .Select(mod => new QuasarModSelection
                {
                    WorkshopId = mod.WorkshopId,
                    DisplayName = mod.DisplayName,
                    IsDependency = mod.IsDependency,
                })
                .ToList();
        }

        return profile;
    }

    private static void CopyOptions(
        QuasarConfigProfile source,
        QuasarConfigProfile target,
        QuasarConfigOptionScope scope,
        Func<QuasarConfigOptionDefinition, bool> predicate)
    {
        foreach (var option in QuasarConfigMetadata.Options.Where(option => option.Scope == scope && predicate(option)))
        {
            var property = QuasarConfigMetadata.GetProperty(option);
            object sourceObject = scope == QuasarConfigOptionScope.Root ? source.RootSettings : source.SessionSettings;
            object targetObject = scope == QuasarConfigOptionScope.Root ? target.RootSettings : target.SessionSettings;
            var value = property.GetValue(sourceObject);
            if (value is Dictionary<string, int> dictionary)
                value = new Dictionary<string, int>(dictionary, StringComparer.OrdinalIgnoreCase);
            property.SetValue(targetObject, value);
        }
    }

    private int AllocateAvailablePort(int preferredPort)
    {
        var port = preferredPort is >= 1 and <= 65535 ? preferredPort : 27016;
        var used = servers.GetServers().Select(server => server.ServerPort).ToHashSet();
        while (used.Contains(port) && port < 65535)
            port++;
        if (used.Contains(port))
            throw new InvalidOperationException("No available server port found.");
        return port;
    }

    private static void ValidateMoveSources(
        ExistingServerImportAnalysis analysis,
        IEnumerable<ExistingServerWorldCandidate> worlds)
    {
        var quasarRoot = Path.GetFullPath(MagnetarPaths.GetQuasarDirectory());
        foreach (var world in worlds)
        {
            if (DedicatedServerPathResolver.IsPathWithinRoot(world.Path, quasarRoot))
                throw new InvalidOperationException($"Cannot move source world '{world.Path}' because it is already inside Quasar storage.");
            if (PathsEqual(world.Path, analysis.SourcePath) || PathsEqual(world.Path, analysis.AppDataPath))
                throw new InvalidOperationException($"Cannot move source world '{world.Path}' because it is also a source server root. Use Copy mode.");
            if ((File.GetAttributes(world.Path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException($"Cannot move symlinked source world '{world.Path}'. Use Copy mode.");
        }
    }

    private async Task RollBackAsync(
        DedicatedServerDefinition? server,
        QuasarConfigProfile? profile,
        IReadOnlyList<QuasarWorldTemplate> templates,
        string? serverRoot)
    {
        try
        {
            if (server is not null && servers.GetServer(server.UniqueName) is not null)
                await servers.DeleteAsync(server.UniqueName);
            if (profile is not null && !string.IsNullOrWhiteSpace(profile.ConfigProfileId))
                await profiles.DeleteAsync(profile.ConfigProfileId);
            foreach (var template in templates.Reverse())
                await worldTemplates.DeleteAsync(template.WorldTemplateId);
            if (!string.IsNullOrWhiteSpace(serverRoot) && Directory.Exists(serverRoot))
                Directory.Delete(serverRoot, recursive: true);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to fully roll back an existing-server import.");
        }
    }

    private static async Task CopyWorldDirectoryAsync(
        string sourcePath,
        string targetPath,
        CancellationToken cancellationToken)
    {
        if (Directory.Exists(targetPath))
            throw new InvalidOperationException($"Managed world target already exists: {targetPath}");

        await Task.Run(() =>
        {
            Directory.CreateDirectory(targetPath);
            foreach (var sourceFile in Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(sourcePath, sourceFile);
                var firstSegment = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
                if (string.Equals(firstSegment, "Backup", StringComparison.OrdinalIgnoreCase))
                    continue;

                var targetFile = Path.Combine(targetPath, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
                File.Copy(sourceFile, targetFile, overwrite: false);
            }
        }, cancellationToken);
    }

    private static string NormalizeName(params string[] candidates) =>
        candidates.Select(candidate => candidate?.Trim())
            .FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate)) ?? "Imported Server";

    private static string NormalizeIp(string value) =>
        string.IsNullOrWhiteSpace(value) ? "0.0.0.0" : value.Trim();

    private static string FormatKind(ExistingServerKind kind) =>
        kind == ExistingServerKind.Torch ? "Torch" : "vanilla Dedicated Server";

    private static bool PathsEqual(string left, string right) =>
        DedicatedServerPathResolver.PathsEqual(left, right);

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}

public static class ExistingServerImportAnalyzer
{
    public static ExistingServerImportAnalysis Analyze(ExistingServerKind kind, string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new InvalidOperationException("Source server folder required.");

        var sourceRoot = Path.GetFullPath(sourcePath.Trim());
        if (!Directory.Exists(sourceRoot))
            throw new DirectoryNotFoundException($"Source server folder not found: {sourceRoot}");

        var warnings = new List<string>();
        var torch = kind == ExistingServerKind.Torch
            ? FindTorchLayout(sourceRoot)
            : null;
        var appDataPath = torch?.AppDataPath ?? FindVanillaAppDataPath(sourceRoot, warnings);
        var configPath = Path.Combine(appDataPath, "SpaceEngineers-Dedicated.cfg");
        var config = ReadDedicatedConfig(configPath);
        var worldPaths = FindWorldPaths(sourceRoot, appDataPath, config.LoadWorld);
        if (worldPaths.Count == 0)
            throw new InvalidOperationException($"No world folders containing Sandbox.sbc found under '{appDataPath}'.");

        var configuredPath = ResolveForeignPath(config.LoadWorld, appDataPath, sourceRoot);
        var worlds = new List<ExistingServerWorldCandidate>();
        foreach (var worldPath in worldPaths)
        {
            var worldProfile = new QuasarConfigProfile();
            var sessionSettingCount = 0;
            var modCount = 0;
            var sandboxConfigPath = Path.Combine(worldPath, WorldSandboxConfigEditor.SandboxConfigFileName);
            if (File.Exists(sandboxConfigPath))
            {
                try
                {
                    var import = WorldSandboxConfigEditor.ReadConfigProfile(sandboxConfigPath, includeOnlineMode: true);
                    worldProfile = import.Profile;
                    sessionSettingCount = import.SessionSettingCount;
                    modCount = import.ModCount;
                }
                catch (Exception exception)
                {
                    warnings.Add($"World '{Path.GetFileName(worldPath)}' settings could not be read: {exception.Message}");
                }
            }
            else
            {
                warnings.Add($"World '{Path.GetFileName(worldPath)}' has no {WorldSandboxConfigEditor.SandboxConfigFileName}; world data can still be imported.");
            }

            worlds.Add(new ExistingServerWorldCandidate
            {
                Name = ReadWorldName(sandboxConfigPath, Path.GetFileName(worldPath)),
                Path = worldPath,
                IsConfiguredWorld = configuredPath is not null && PathsEqual(configuredPath, worldPath),
                SessionSettingCount = sessionSettingCount,
                ModCount = modCount,
                ImportedProfile = worldProfile,
            });
        }

        if (worlds.All(world => !world.IsConfiguredWorld))
        {
            var configuredLeaf = GetForeignFileName(config.LoadWorld);
            var matched = worlds.FirstOrDefault(world => string.Equals(
                Path.GetFileName(world.Path),
                configuredLeaf,
                StringComparison.OrdinalIgnoreCase));
            if (matched is not null)
            {
                var index = worlds.IndexOf(matched);
                worlds[index] = new ExistingServerWorldCandidate
                {
                    Name = matched.Name,
                    Path = matched.Path,
                    IsConfiguredWorld = true,
                    SessionSettingCount = matched.SessionSettingCount,
                    ModCount = matched.ModCount,
                    ImportedProfile = matched.ImportedProfile,
                };
            }
        }

        worlds = worlds
            .OrderByDescending(world => world.IsConfiguredWorld)
            .ThenBy(world => world.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (config.HasPasswordHash)
            warnings.Add("Source password is stored as a one-way hash and cannot be transferred. Set a new password in imported config profile.");
        if (torch is { PluginCount: > 0 })
            warnings.Add($"Detected {torch.PluginCount} Torch plugin entries. Torch plugins are not Magnetar plugins and will not be transferred.");
        if (torch is { WhitelistEnabled: true } or { WhitelistEntryCount: > 0 })
            warnings.Add("Torch's independent whitelist has no vanilla DS/Magnetar equivalent and cannot be transferred. DS GroupID whitelist, administrators, reserved slots, and bans are imported separately.");
        if (torch is { Autostart: true })
            warnings.Add("Torch Autostart detected. Imported server stays stopped until first review and manual start.");

        var primary = worlds.First();
        return new ExistingServerImportAnalysis
        {
            Kind = kind,
            SourcePath = sourceRoot,
            AppDataPath = appDataPath,
            ConfigPath = configPath,
            ServerName = NormalizeName(config.ServerName, Path.GetFileName(sourceRoot)),
            WorldName = NormalizeName(config.WorldName, primary.Name),
            ServerIp = string.IsNullOrWhiteSpace(config.ServerIp) ? "0.0.0.0" : config.ServerIp,
            ServerPort = config.ServerPort is >= 1 and <= 65535 ? config.ServerPort : 27016,
            RootSettingCount = config.RootSettingCount,
            SessionSettingCount = primary.SessionSettingCount > 0
                ? primary.SessionSettingCount
                : config.SessionSettingCount,
            AccessEntryCount = config.AccessEntryCount,
            HasPasswordHash = config.HasPasswordHash,
            TorchAutostart = torch?.Autostart ?? false,
            TorchRestartOnCrash = torch?.RestartOnCrash ?? true,
            TorchPluginCount = torch?.PluginCount ?? 0,
            TorchWhitelistEnabled = torch?.WhitelistEnabled ?? false,
            TorchWhitelistEntryCount = torch?.WhitelistEntryCount ?? 0,
            Worlds = worlds,
            Warnings = warnings,
            ImportedProfile = config.Profile,
        };
    }

    private static TorchLayout FindTorchLayout(string sourceRoot)
    {
        var torchConfigPath = Path.Combine(sourceRoot, "Torch.cfg");
        if (!File.Exists(torchConfigPath))
        {
            var parent = Directory.GetParent(sourceRoot)?.FullName;
            if (!string.IsNullOrWhiteSpace(parent) && File.Exists(Path.Combine(parent, "Torch.cfg")))
            {
                torchConfigPath = Path.Combine(parent, "Torch.cfg");
                sourceRoot = parent;
            }
            else
            {
                throw new InvalidOperationException($"Torch.cfg not found in '{sourceRoot}'. Select Torch's root folder.");
            }
        }

        var document = XDocument.Load(torchConfigPath, LoadOptions.PreserveWhitespace);
        var root = document.Root ?? throw new InvalidOperationException($"Torch config '{torchConfigPath}' has no root element.");
        var configuredInstance = ElementValue(root, "InstancePath");
        var candidates = new[]
            {
                sourceRoot,
                Path.Combine(sourceRoot, "Instance"),
                ResolveForeignPath(configuredInstance, sourceRoot, sourceRoot),
            }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path!))
            .Distinct(PathComparer)
            .ToList();
        var appDataPath = candidates.FirstOrDefault(path =>
            File.Exists(Path.Combine(path, "SpaceEngineers-Dedicated.cfg")));
        if (appDataPath is null)
            throw new InvalidOperationException($"Torch instance SpaceEngineers-Dedicated.cfg not found below '{sourceRoot}'.");

        var pluginCount = new[] { "Plugins", "LocalPlugins" }
            .SelectMany(name => ElementIgnoreCase(root, name)?.Descendants() ?? [])
            .Count(element => !element.HasElements && !string.IsNullOrWhiteSpace(element.Value));
        var whitelist = ElementIgnoreCase(root, "Whitelist");
        var whitelistEntryCount = (whitelist?.Descendants() ?? [])
            .Count(element => !element.HasElements && !string.IsNullOrWhiteSpace(element.Value));

        return new TorchLayout(
            appDataPath,
            ReadBool(root, "Autostart"),
            ReadBool(root, "RestartOnCrash"),
            pluginCount,
            ReadBool(root, "EnableWhitelist"),
            whitelistEntryCount);
    }

    private static string FindVanillaAppDataPath(string sourceRoot, ICollection<string> warnings)
    {
        var matches = EnumerateDirectories(sourceRoot, maxDepth: 2)
            .Where(directory => File.Exists(Path.Combine(directory, "SpaceEngineers-Dedicated.cfg")))
            .ToList();
        if (matches.Count == 0)
            throw new InvalidOperationException($"SpaceEngineers-Dedicated.cfg not found in '{sourceRoot}' or its first two folder levels.");
        if (matches.Count > 1)
            warnings.Add($"Found {matches.Count} DS configs; using '{Path.Combine(matches[0], "SpaceEngineers-Dedicated.cfg")}'. Import instances separately to choose another.");
        return matches[0];
    }

    private static IReadOnlyList<string> FindWorldPaths(string sourceRoot, string appDataPath, string loadWorld)
    {
        var results = new List<string>();
        var configured = ResolveForeignPath(loadWorld, appDataPath, sourceRoot);
        AddWorld(configured);

        var savesPath = Path.Combine(appDataPath, "Saves");
        if (Directory.Exists(savesPath))
        {
            foreach (var directory in Directory.EnumerateDirectories(savesPath))
                AddWorld(directory);
        }

        return results;

        void AddWorld(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;
            var fullPath = Path.GetFullPath(path);
            if (!File.Exists(Path.Combine(fullPath, "Sandbox.sbc")))
                return;
            if (results.All(existing => !PathsEqual(existing, fullPath)))
                results.Add(fullPath);
        }
    }

    private static DedicatedConfigImport ReadDedicatedConfig(string configPath)
    {
        var document = XDocument.Load(configPath, LoadOptions.PreserveWhitespace);
        var root = document.Root ?? throw new InvalidOperationException($"DS config '{configPath}' has no root element.");
        var profile = new QuasarConfigProfile();
        var rootSettingCount = ApplyOptions(root, profile.RootSettings, QuasarConfigOptionScope.Root);
        var sessionElement = ElementIgnoreCase(root, "SessionSettings");
        var sessionSettingCount = sessionElement is null
            ? 0
            : ApplyOptions(sessionElement, profile.SessionSettings, QuasarConfigOptionScope.Session);

        profile.RootSettings.GroupId = ReadUnsignedLong(root, "GroupID");
        profile.RootSettings.Administrators = ReadStringList(root, "Administrators");
        profile.RootSettings.Reserved = ReadUnsignedLongList(root, "Reserved");
        profile.RootSettings.Banned = ReadUnsignedLongList(root, "Banned");
        var accessEntryCount = (profile.RootSettings.GroupId > 0 ? 1 : 0) +
                               profile.RootSettings.Administrators.Count +
                               profile.RootSettings.Reserved.Count +
                               profile.RootSettings.Banned.Count;

        return new DedicatedConfigImport(
            profile,
            ElementValue(root, "ServerName"),
            ElementValue(root, "WorldName"),
            FirstNonEmpty(ElementValue(root, "IP"), ElementValue(root, "ServerIP")),
            ReadInt(root, "ServerPort", 27016),
            ElementValue(root, "LoadWorld"),
            rootSettingCount,
            sessionSettingCount,
            accessEntryCount,
            !string.IsNullOrWhiteSpace(ElementValue(root, "ServerPasswordHash")));
    }

    private static int ApplyOptions(XElement parent, object target, QuasarConfigOptionScope scope)
    {
        var count = 0;
        foreach (var option in QuasarConfigMetadata.Options.Where(option => option.Scope == scope))
        {
            if (string.IsNullOrWhiteSpace(option.ElementName))
                continue;
            var element = ElementIgnoreCase(parent, option.ElementName);
            if (element is null)
                continue;
            var property = QuasarConfigMetadata.GetProperty(option);
            if (!WorldSandboxConfigEditor.TryReadConfigOptionValue(option, property, element, out var value))
                continue;
            property.SetValue(target, value);
            count++;
        }
        return count;
    }

    private static IEnumerable<string> EnumerateDirectories(string root, int maxDepth)
    {
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            yield return current.Path;
            if (current.Depth >= maxDepth)
                continue;

            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(current.Path).OrderBy(path => path, PathComparer).ToList();
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var child in children)
                queue.Enqueue((child, current.Depth + 1));
        }
    }

    private static string? ResolveForeignPath(string? value, string appDataPath, string sourceRoot)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var normalized = value.Trim().Replace('\\', '/');
        var candidates = new List<string>();
        if (!OperatingSystem.IsWindows() && normalized.Length >= 3 &&
            char.IsLetter(normalized[0]) && normalized[1] == ':' && normalized[2] == '/')
        {
            if (char.ToUpperInvariant(normalized[0]) == 'Z')
                candidates.Add("/" + normalized[3..].TrimStart('/'));
        }
        else if (Path.IsPathRooted(normalized))
        {
            candidates.Add(normalized);
        }
        else
        {
            candidates.Add(Path.Combine(appDataPath, normalized));
        }

        var leaf = GetForeignFileName(normalized);
        if (!string.IsNullOrWhiteSpace(leaf))
        {
            candidates.Add(Path.Combine(appDataPath, "Saves", leaf));
            candidates.Add(Path.Combine(sourceRoot, leaf));
        }

        return candidates
            .Select(Path.GetFullPath)
            .FirstOrDefault(Directory.Exists)
            ?? candidates.Select(Path.GetFullPath).FirstOrDefault();
    }

    private static string ReadWorldName(string sandboxConfigPath, string fallback)
    {
        if (!File.Exists(sandboxConfigPath))
            return NormalizeName(fallback);
        try
        {
            var root = XDocument.Load(sandboxConfigPath).Root;
            return NormalizeName(root is null ? string.Empty : ElementValue(root, "SessionName"), fallback);
        }
        catch
        {
            return NormalizeName(fallback);
        }
    }

    private static List<string> ReadStringList(XElement root, string name) =>
        (ElementIgnoreCase(root, name)?.Elements() ?? [])
        .Select(element => element.Value.Trim())
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    private static List<ulong> ReadUnsignedLongList(XElement root, string name) =>
        (ElementIgnoreCase(root, name)?.Elements() ?? [])
        .Select(element => ulong.TryParse(element.Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0)
        .Where(value => value > 0)
        .Distinct()
        .ToList();

    private static ulong ReadUnsignedLong(XElement root, string name) =>
        ulong.TryParse(ElementValue(root, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;

    private static int ReadInt(XElement root, string name, int fallback) =>
        int.TryParse(ElementValue(root, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private static bool ReadBool(XElement root, string name) =>
        bool.TryParse(ElementValue(root, name), out var value) && value;

    private static XElement? ElementIgnoreCase(XElement parent, string name) =>
        parent.Elements().FirstOrDefault(element =>
            string.Equals(element.Name.LocalName, name, StringComparison.OrdinalIgnoreCase));

    private static string ElementValue(XElement parent, string name) =>
        ElementIgnoreCase(parent, name)?.Value.Trim() ?? string.Empty;

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static string NormalizeName(params string[] candidates) =>
        candidates.Select(candidate => candidate?.Trim())
            .FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate)) ?? "Imported Server";

    private static string GetForeignFileName(string? path)
    {
        var normalized = (path ?? string.Empty).Trim().Replace('\\', '/').TrimEnd('/');
        var index = normalized.LastIndexOf('/');
        return index >= 0 ? normalized[(index + 1)..] : normalized;
    }

    private static bool PathsEqual(string left, string right) =>
        DedicatedServerPathResolver.PathsEqual(left, right);

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private sealed record TorchLayout(
        string AppDataPath,
        bool Autostart,
        bool RestartOnCrash,
        int PluginCount,
        bool WhitelistEnabled,
        int WhitelistEntryCount);

    private sealed record DedicatedConfigImport(
        QuasarConfigProfile Profile,
        string ServerName,
        string WorldName,
        string ServerIp,
        int ServerPort,
        string LoadWorld,
        int RootSettingCount,
        int SessionSettingCount,
        int AccessEntryCount,
        bool HasPasswordHash);
}
