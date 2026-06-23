using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Quasar.Models;

namespace Quasar.Services;

public sealed class DedicatedServerRuntimePreparer
{
    private static readonly Regex IgnoreLastSessionPattern = new(@"(?<!\S)-ignorelastsession(?!\S)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex ConsolePattern = new(@"(?<!\S)-console(?!\S)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex NoConsolePattern = new(@"(?<!\S)-noconsole(?!\S)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex PathOptionPattern = new(@"(?<!\S)-path(?!\S)(?:\s+(?:""(?:""""|\\.|[^""])*""|\S+))?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex ConfigOptionPattern = new(@"(?<!\S)-config(?!\S)(?:\s+(?:""(?:""""|\\.|[^""])*""|\S+))?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex Ds64OptionPattern = new(@"(?<!\S)-ds64(?!\S)(?:\s+(?:""(?:""""|\\.|[^""])*""|\S+))?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex NoSplashPattern = new(@"(?<!\S)-nosplash(?!\S)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex DaemonPattern = new(@"(?<!\S)-daemon(?!\S)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex NoImplicitModPattern = new(@"(?<!\S)-noimplicitmod(?!\S)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex ConsentOptionPattern = new(@"(?<!\S)-(?:no)?consent(?!\S)|(?<!\S)-withdraw-consent(?!\S)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly XNamespace XsiNamespace = "http://www.w3.org/2001/XMLSchema-instance";
    private static readonly XNamespace XsdNamespace = "http://www.w3.org/2001/XMLSchema";

    private readonly ILogger<DedicatedServerRuntimePreparer> _logger;
    private readonly WebServiceOptions _options;
    private readonly DataHandlingConsentCatalog _dataHandlingConsent;
    private readonly QuasarConfigProfileCatalog _configProfiles;
    private readonly QuasarWorldTemplateCatalog _worldTemplates;
    private readonly QuasarPluginCatalogService _pluginCatalog;
    private readonly QuasarDevFolderCatalog _devFolderCatalog;

    public DedicatedServerRuntimePreparer(
        ILogger<DedicatedServerRuntimePreparer> logger,
        WebServiceOptions options,
        DataHandlingConsentCatalog dataHandlingConsent,
        QuasarConfigProfileCatalog configProfiles,
        QuasarWorldTemplateCatalog worldTemplates,
        QuasarPluginCatalogService pluginCatalog,
        QuasarDevFolderCatalog devFolderCatalog)
    {
        _logger = logger;
        _options = options;
        _dataHandlingConsent = dataHandlingConsent;
        _configProfiles = configProfiles;
        _worldTemplates = worldTemplates;
        _pluginCatalog = pluginCatalog;
        _devFolderCatalog = devFolderCatalog;
    }

    public async Task<PreparedDedicatedServerLaunch> PrepareAsync(
        DedicatedServerDefinition definition,
        string dedicatedServer64Path,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var dedicatedServerAppDataPath = RequirePath(definition.DedicatedServerAppDataPath, "DedicatedServerAppDataPath");
        var magnetarAppDataPath = RequirePath(definition.MagnetarAppDataPath, "MagnetarAppDataPath");
        var configProfile = ResolveConfigProfile(definition);
        var worldPath = await ResolveOrSeedWorldPathAsync(definition, cancellationToken);
        var runtimeConfigPath = Path.Combine(dedicatedServerAppDataPath, "SpaceEngineers-Dedicated.cfg");
        var lastSessionPath = Path.Combine(dedicatedServerAppDataPath, "Saves", "LastSession.sbl");

        Directory.CreateDirectory(dedicatedServerAppDataPath);
        Directory.CreateDirectory(magnetarAppDataPath);
        Directory.CreateDirectory(Path.Combine(dedicatedServerAppDataPath, "Saves"));

        await PrepareRuntimeConfigAsync(definition, configProfile, worldPath, runtimeConfigPath, cancellationToken);
        await PrepareMagnetarConfigAsync(definition, configProfile, magnetarAppDataPath, cancellationToken);
        await PrepareWorldConfigAsync(definition, configProfile, worldPath, cancellationToken);
        await WriteLastSessionAsync(definition, worldPath, dedicatedServerAppDataPath, lastSessionPath, cancellationToken);

        var arguments = BuildLaunchArguments(
            definition,
            dedicatedServerAppDataPath,
            magnetarAppDataPath,
            dedicatedServer64Path,
            worldPath,
            runtimeConfigPath,
            _options,
            _dataHandlingConsent.GetSettings().ConsentGranted);

        return new PreparedDedicatedServerLaunch(
            dedicatedServerAppDataPath,
            magnetarAppDataPath,
            dedicatedServer64Path,
            worldPath,
            runtimeConfigPath,
            lastSessionPath,
            arguments);
    }

    public AgentDeploymentComparison GetAgentDeploymentComparison(DedicatedServerDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var sourceDirectory = LocateAgentSourceDirectory();
        var bundledPath = sourceDirectory is null ? string.Empty : Path.Combine(sourceDirectory, AgentPluginFileName);
        var deployedPath = string.IsNullOrWhiteSpace(definition.MagnetarAppDataPath)
            ? string.Empty
            : Path.Combine(definition.MagnetarAppDataPath, "Local", AgentPluginFileName);

        return new AgentDeploymentComparison(
            bundledPath,
            TryComputeSha256Hex(bundledPath),
            deployedPath,
            TryComputeSha256Hex(deployedPath));
    }

    private async Task PrepareRuntimeConfigAsync(
        DedicatedServerDefinition definition,
        QuasarConfigProfile configProfile,
        string worldPath,
        string runtimeConfigPath,
        CancellationToken cancellationToken)
    {
        var configuredSourcePath = string.IsNullOrWhiteSpace(definition.ConfigFilePath)
            ? runtimeConfigPath
            : definition.ConfigFilePath.Trim();

        var sourcePath = File.Exists(configuredSourcePath)
            ? configuredSourcePath
            : File.Exists(runtimeConfigPath)
                ? runtimeConfigPath
                : null;

        XDocument document;
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            document = CreateEmptyDedicatedConfigDocument();
        }
        else
        {
            try
            {
                document = XDocument.Load(sourcePath, LoadOptions.PreserveWhitespace);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException($"Failed to load DS config '{sourcePath}'.", exception);
            }
        }

        var root = document.Root ?? throw new InvalidOperationException($"DS config '{sourcePath ?? runtimeConfigPath}' has no root element.");
        UpsertElement(root, "IgnoreLastSession", "false");

        if (definition.ServerPort > 0)
        {
            UpsertElement(root, "ServerPort", definition.ServerPort.ToString(CultureInfo.InvariantCulture));

            // Derive the Steam and Remote API ports from the game port so multiple servers on
            // one host never collide on the SE defaults (8766 / 8080). A shared SteamPort leaves
            // the later-starting server unreachable even though its ServerPort is bound.
            UpsertElement(root, "SteamPort", (definition.ServerPort + 1000).ToString(CultureInfo.InvariantCulture));
            UpsertElement(root, "RemoteApiPort", (definition.ServerPort + 2000).ToString(CultureInfo.InvariantCulture));
        }

        if (!string.IsNullOrWhiteSpace(definition.ServerIP))
            UpsertElement(root, "ServerIP", definition.ServerIP.Trim());

        ApplyConfigProfile(root, configProfile);
        UpsertElement(root, "ServerName", GetServerDisplayName(definition));
        UpsertElement(root, "WorldName", GetWorldDisplayName(definition, worldPath));

        var content = SerializeXml(document);
        await AtomicFileWriter.WriteTextAsync(runtimeConfigPath, content, cancellationToken);

        _logger.LogInformation("Prepared runtime DS config for server {UniqueName} at {Path}.", definition.UniqueName, runtimeConfigPath);
    }

    private async Task WriteLastSessionAsync(
        DedicatedServerDefinition definition,
        string worldPath,
        string dedicatedServerAppDataPath,
        string lastSessionPath,
        CancellationToken cancellationToken)
    {
        var savesPath = Path.Combine(dedicatedServerAppDataPath, "Saves");
        var relativePath = TryGetRelativePath(savesPath, worldPath);
        var gameName = GetWorldDisplayName(definition, worldPath);

        var document = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(
                "MyObjectBuilder_LastSession",
                new XAttribute(XNamespace.Xmlns + "xsi", XsiNamespace),
                new XAttribute(XNamespace.Xmlns + "xsd", XsdNamespace),
                new XElement("Path", worldPath),
                new XElement("IsContentWorlds", "false"),
                new XElement("IsOnline", "false"),
                new XElement("IsLobby", "false"),
                new XElement("GameName", gameName),
                string.IsNullOrWhiteSpace(relativePath) ? null : new XElement("RelativePath", relativePath)));

        await AtomicFileWriter.WriteTextAsync(lastSessionPath, SerializeXml(document), cancellationToken);
        _logger.LogInformation("Prepared LastSession.sbl for server {UniqueName} at {Path}.", definition.UniqueName, lastSessionPath);
    }

    private async Task PrepareMagnetarConfigAsync(
        DedicatedServerDefinition definition,
        QuasarConfigProfile configProfile,
        string magnetarAppDataPath,
        CancellationToken cancellationToken)
    {
        var sourcesDirectory = Path.Combine(magnetarAppDataPath, "Sources");
        var profilesDirectory = Path.Combine(magnetarAppDataPath, "Profiles");
        var localDirectory = Path.Combine(magnetarAppDataPath, "Local");
        Directory.CreateDirectory(sourcesDirectory);
        Directory.CreateDirectory(profilesDirectory);
        Directory.CreateDirectory(localDirectory);

        var agentLocalFileNames = await DeployQuasarAgentAsync(definition, localDirectory, cancellationToken);

        var currentTemplateName = string.IsNullOrWhiteSpace(configProfile.Name)
            ? "Quasar Current"
            : configProfile.Name.Trim();
        var remotePluginSources = await BuildRemotePluginSourcesAsync(configProfile, cancellationToken);
        var devFolders = _devFolderCatalog.GetDevFolders();
        var localDevFolderIds = GetDevFolderPluginIdSet(devFolders);
        var selectedPluginIds = GetSelectedPluginIdSet(configProfile);
        var selectedDevFolders = devFolders
            .Where(devFolder => selectedPluginIds.Contains(QuasarPluginCatalogService.GetDevFolderPluginId(devFolder)))
            .ToList();

        var sourcesDocument = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(
                "SourcesConfig",
                new XElement("ShowWarning", "true"),
                new XElement("MaxSourceAge", "2"),
                new XElement("LocalHubSources"),
                new XElement(
                    "RemoteHubSources",
                    remotePluginSources.UseDefaultHub
                        ? CreateDefaultRemoteHubElement()
                        : null),
                new XElement(
                    "RemotePluginSources",
                    remotePluginSources.Entries.Select(CreateRemotePluginElement)),
                new XElement(
                    "LocalPluginSources",
                    devFolders
                    .Select(devFolder => new XElement(
                        "LocalPlugin",
                        new XElement("Name", QuasarPluginCatalogService.GetDevFolderPluginId(devFolder)),
                        new XElement("Folder", devFolder.FolderPath),
                        new XElement("Enabled", "true")))),
                new XElement(
                    "ModSources",
                    configProfile.Mods
                    .Select(mod => new XElement(
                        "Mod",
                        new XElement("Name", string.IsNullOrWhiteSpace(mod.DisplayName) ? mod.WorkshopId.ToString(CultureInfo.InvariantCulture) : mod.DisplayName),
                        new XElement("ID", mod.WorkshopId.ToString(CultureInfo.InvariantCulture)),
                        new XElement("Enabled", "true"))))));

        // Mods are written authoritatively into the world's Sandbox_config.sbc by
        // PrepareWorldConfigAsync; Magnetar's profile override is intentionally
        // empty so it cannot drift from the world's mod list.
        var currentProfileDocument = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(
                "Profile",
                new XElement("Name", currentTemplateName),
                new XElement(
                    "GitHub",
                    configProfile.Plugins
                    .Where(plugin => QuasarPluginCatalogService.IsManualSelectionAllowed(plugin.PluginId))
                    .Where(plugin => !localDevFolderIds.Contains(plugin.PluginId.Trim()))
                    .Select(plugin => new XElement(
                        "GitHubPluginConfig",
                        new XElement("Id", plugin.PluginId),
                        new XElement("SelectedVersion", plugin.SelectedVersion)))),
                new XElement(
                    "DevFolder",
                    selectedDevFolders
                    .Select(devFolder => new XElement(
                        "LocalFolderConfig",
                        new XElement("Id", QuasarPluginCatalogService.GetDevFolderPluginId(devFolder)),
                        new XElement("DataFile", devFolder.DataFile),
                        new XElement("DebugBuild", devFolder.DebugBuild ? "true" : "false")))),
                new XElement(
                    "Local",
                    agentLocalFileNames.Select(fileName => new XElement("string", fileName))),
                new XElement("Mods")));

        await AtomicFileWriter.WriteTextAsync(
            Path.Combine(sourcesDirectory, "sources.xml"),
            SerializeXml(sourcesDocument),
            cancellationToken);

        await AtomicFileWriter.WriteTextAsync(
            Path.Combine(profilesDirectory, "Current.xml"),
            SerializeXml(currentProfileDocument),
            cancellationToken);
    }

    private async Task<RemotePluginSourceSet> BuildRemotePluginSourcesAsync(
        QuasarConfigProfile configProfile,
        CancellationToken cancellationToken)
    {
        var catalogEntries = _pluginCatalog.GetEntries();
        var localDevFolderIds = GetDevFolderPluginIdSet(_devFolderCatalog.GetDevFolders());
        var selectedPluginIds = configProfile.Plugins
            .Where(plugin => QuasarPluginCatalogService.IsManualSelectionAllowed(plugin.PluginId))
            .Select(plugin => plugin.PluginId.Trim())
            .Where(pluginId => !string.IsNullOrWhiteSpace(pluginId))
            .Where(pluginId => !localDevFolderIds.Contains(pluginId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var catalogById = ToCatalogById(catalogEntries);

        if (selectedPluginIds.Any(pluginId => !HasCatalogRemoteManifest(catalogById, pluginId)))
        {
            try
            {
                await _pluginCatalog.RefreshAsync(cancellationToken);
                catalogById = ToCatalogById(_pluginCatalog.GetEntries());
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Failed refreshing plugin catalog before preparing Magnetar plugin sources.");
            }
        }

        var entries = new Dictionary<string, QuasarPluginCatalogEntry>(StringComparer.OrdinalIgnoreCase);
        var useDefaultHub = false;

        foreach (var pluginId in selectedPluginIds)
        {
            if (catalogById.TryGetValue(pluginId, out var catalogEntry) &&
                HasRemoteManifest(catalogEntry))
            {
                entries[pluginId] = catalogEntry;
                continue;
            }

            useDefaultHub = true;
        }

        AddCoreRemotePluginSource(entries, catalogById, QuasarPluginCatalogService.DotNetCompatPluginId, QuasarPluginCatalogService.DotNetCompatManifestFile);
        if (OperatingSystem.IsLinux())
            AddCoreRemotePluginSource(entries, catalogById, QuasarPluginCatalogService.LinuxCompatPluginId, QuasarPluginCatalogService.LinuxCompatManifestFile);

        return new RemotePluginSourceSet(useDefaultHub, entries.Values.OrderBy(entry => entry.Hidden).ThenBy(entry => entry.FriendlyName, StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static Dictionary<string, QuasarPluginCatalogEntry> ToCatalogById(IReadOnlyList<QuasarPluginCatalogEntry> entries) =>
        entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.PluginId))
            .ToDictionary(entry => entry.PluginId, StringComparer.OrdinalIgnoreCase);

    private static HashSet<string> GetSelectedPluginIdSet(QuasarConfigProfile configProfile) =>
        configProfile.Plugins
            .Where(plugin => QuasarPluginCatalogService.IsManualSelectionAllowed(plugin.PluginId))
            .Select(plugin => plugin.PluginId.Trim())
            .Where(pluginId => !string.IsNullOrWhiteSpace(pluginId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static HashSet<string> GetDevFolderPluginIdSet(IReadOnlyList<QuasarDevFolderSelection> devFolders) =>
        devFolders
            .Select(QuasarPluginCatalogService.GetDevFolderPluginId)
            .Where(pluginId => !string.IsNullOrWhiteSpace(pluginId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static bool HasCatalogRemoteManifest(
        IReadOnlyDictionary<string, QuasarPluginCatalogEntry> catalogById,
        string pluginId) =>
        catalogById.TryGetValue(pluginId, out var catalogEntry) &&
        HasRemoteManifest(catalogEntry);

    private static void AddCoreRemotePluginSource(
        Dictionary<string, QuasarPluginCatalogEntry> entries,
        IReadOnlyDictionary<string, QuasarPluginCatalogEntry> catalogById,
        string pluginId,
        string manifestFile)
    {
        if (catalogById.TryGetValue(pluginId, out var catalogEntry) &&
            HasRemoteManifest(catalogEntry))
        {
            entries[pluginId] = catalogEntry;
            return;
        }

        entries[pluginId] = new QuasarPluginCatalogEntry
        {
            PluginId = pluginId,
            FriendlyName = pluginId,
            ManifestRepo = QuasarPluginCatalogService.DefaultHubRepo,
            ManifestBranch = QuasarPluginCatalogService.DefaultHubBranch,
            ManifestFile = manifestFile,
            Hidden = true,
        };
    }

    private static bool HasRemoteManifest(QuasarPluginCatalogEntry entry) =>
        !string.IsNullOrWhiteSpace(entry.ManifestRepo) &&
        !string.IsNullOrWhiteSpace(entry.ManifestBranch) &&
        !string.IsNullOrWhiteSpace(entry.ManifestFile);

    private static XElement CreateDefaultRemoteHubElement() =>
        new(
            "RemoteHub",
            new XElement("Name", QuasarPluginCatalogService.DefaultHubName),
            new XElement("Repo", QuasarPluginCatalogService.DefaultHubRepo),
            new XElement("Branch", QuasarPluginCatalogService.DefaultHubBranch),
            new XElement("Enabled", "true"),
            new XElement("Trusted", "true"));

    private static XElement CreateRemotePluginElement(QuasarPluginCatalogEntry entry) =>
        new(
            "RemotePlugin",
            new XElement("Name", string.IsNullOrWhiteSpace(entry.FriendlyName) ? entry.PluginId : entry.FriendlyName),
            new XElement("Repo", entry.ManifestRepo),
            new XElement("Branch", entry.ManifestBranch),
            new XElement("File", entry.ManifestFile),
            new XElement("Enabled", "true"),
            new XElement("Trusted", "true"));

    private async Task<IReadOnlyList<string>> DeployQuasarAgentAsync(
        DedicatedServerDefinition definition,
        string localPluginDirectory,
        CancellationToken cancellationToken)
    {
        var sourceDirectory = LocateAgentSourceDirectory();
        if (sourceDirectory is null)
        {
            _logger.LogWarning("Quasar.Agent.dll could not be located on disk; the agent plugin will not be deployed.");
            return Array.Empty<string>();
        }

        var enabledNames = new List<string>();
        foreach (var fileName in AgentDeploymentFiles)
        {
            var sourcePath = Path.Combine(sourceDirectory, fileName);
            if (!File.Exists(sourcePath))
                continue;

            var destinationPath = Path.Combine(localPluginDirectory, fileName);
            await CopyFileIfChangedAsync(sourcePath, destinationPath, cancellationToken);

            if (string.Equals(fileName, AgentPluginFileName, StringComparison.OrdinalIgnoreCase))
                enabledNames.Add(fileName);
        }

        await DeployHarmonyAsync(definition.ManagedRuntime, sourceDirectory, localPluginDirectory, cancellationToken);

        if (enabledNames.Count == 0)
            _logger.LogWarning("Quasar.Agent.dll was not found in {SourceDirectory}; the agent plugin will not be enabled.", sourceDirectory);

        return enabledNames;
    }

    private async Task DeployHarmonyAsync(
        ManagedServerRuntime runtime,
        string sourceDirectory,
        string localPluginDirectory,
        CancellationToken cancellationToken)
    {
        var effectiveRuntime = OperatingSystem.IsWindows() ? runtime : ManagedServerRuntime.DotNet10;
        var runtimeFolder = effectiveRuntime == ManagedServerRuntime.NetFramework48 ? "NetFramework48" : "DotNet10";
        var sourcePath = Path.Combine(sourceDirectory, runtimeFolder, HarmonyFileName);
        if (!File.Exists(sourcePath))
        {
            _logger.LogWarning(
                "{HarmonyFileName} for runtime {Runtime} was not found under {SourceDirectory}; profiler telemetry will not be available.",
                HarmonyFileName,
                effectiveRuntime,
                sourceDirectory);
            return;
        }

        await CopyFileIfChangedAsync(sourcePath, Path.Combine(localPluginDirectory, HarmonyFileName), cancellationToken);
    }

    private static async Task CopyFileIfChangedAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        if (File.Exists(destinationPath))
        {
            var sourceHash = await ComputeSha256HexAsync(sourcePath, cancellationToken);
            var destinationHash = await ComputeSha256HexAsync(destinationPath, cancellationToken);
            if (string.Equals(sourceHash, destinationHash, StringComparison.OrdinalIgnoreCase))
                return;
        }

        await using var source = File.OpenRead(sourcePath);
        await using var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await source.CopyToAsync(destination, cancellationToken);
    }

    private static async Task<string> ComputeSha256HexAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }

    private static string TryComputeSha256Hex(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return string.Empty;

        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string? LocateAgentSourceDirectory()
    {
        var stagedDirectory = Path.Combine(AppContext.BaseDirectory, "Agent");
        if (File.Exists(Path.Combine(stagedDirectory, AgentPluginFileName)))
            return stagedDirectory;

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; directory is not null && depth < 8; depth++, directory = directory.Parent)
        {
            var devBin = Path.Combine(directory.FullName, "Quasar.Agent", "bin");
            if (!Directory.Exists(devBin))
                continue;

            var candidate = Directory
                .EnumerateFiles(devBin, AgentPluginFileName, SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();

            if (candidate is not null)
                return Path.GetDirectoryName(candidate);
        }

        return null;
    }

    private const string AgentPluginFileName = "Quasar.Agent.dll";
    private const string HarmonyFileName = "0Harmony.dll";

    private static readonly string[] AgentDeploymentFiles =
    [
        AgentPluginFileName,
        "Magnetar.Protocol.dll",
    ];

    private sealed record RemotePluginSourceSet(bool UseDefaultHub, IReadOnlyList<QuasarPluginCatalogEntry> Entries);

    private QuasarConfigProfile ResolveConfigProfile(DedicatedServerDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.ConfigProfileId))
            throw new InvalidOperationException(
                $"Server '{definition.UniqueName}' has no config profile assigned. " +
                "Assign a profile from the Configs page before starting the server.");

        var template = _configProfiles.GetProfile(definition.ConfigProfileId);
        if (template is null)
            throw new InvalidOperationException(
                $"Unknown Quasar config profile '{definition.ConfigProfileId}' for server '{definition.UniqueName}'.");

        return template;
    }

    private async Task PrepareWorldConfigAsync(
        DedicatedServerDefinition definition,
        QuasarConfigProfile configProfile,
        string worldPath,
        CancellationToken cancellationToken)
    {
        var sandboxConfigPath = Path.Combine(worldPath, WorldSandboxConfigEditor.SandboxConfigFileName);
        if (!File.Exists(sandboxConfigPath))
        {
            _logger.LogWarning(
                "Skipping mod list rewrite for server {UniqueName}: {File} not found at {Path}.",
                definition.UniqueName, WorldSandboxConfigEditor.SandboxConfigFileName, sandboxConfigPath);
            return;
        }

        await WorldSandboxConfigEditor.WriteProfileAsync(sandboxConfigPath, configProfile, cancellationToken);
        _logger.LogInformation(
            "Wrote profile '{ProfileName}' settings and {Count} mod entr(y/ies) into {Path}.",
            configProfile.Mods.Count, configProfile.Name, sandboxConfigPath);
    }

    private static XDocument CreateEmptyDedicatedConfigDocument()
    {
        return new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(
                "MyConfigDedicated",
                new XAttribute(XNamespace.Xmlns + "xsd", XsdNamespace),
                new XAttribute(XNamespace.Xmlns + "xsi", XsiNamespace),
                new XElement("SessionSettings")));
    }

    private static void ApplyConfigProfile(XElement root, QuasarConfigProfile configProfile)
    {
        var sessionSettings = root.Element("SessionSettings");
        if (sessionSettings is null)
        {
            sessionSettings = new XElement("SessionSettings");
            root.AddFirst(sessionSettings);
        }

        foreach (var option in QuasarConfigMetadata.Options)
        {
            if (string.IsNullOrWhiteSpace(option.ElementName))
                continue;
            if (option.Kind == QuasarConfigOptionKind.KeyValueText)
                continue;

            var target = option.Scope == QuasarConfigOptionScope.Root
                ? (object)configProfile.RootSettings
                : configProfile.SessionSettings;
            var value = QuasarConfigMetadata.FormatValue(option, target);

            if (option.Scope == QuasarConfigOptionScope.Root)
                UpsertElement(root, option.ElementName, value);
            else
                UpsertElement(sessionSettings, option.ElementName, value);
        }

        UpsertBlockTypeLimits(sessionSettings, configProfile.SessionSettings.BlockTypeLimits);

        UpsertElement(root, "GroupID", configProfile.RootSettings.GroupId.ToString(CultureInfo.InvariantCulture));
        UpsertArray(root, "Administrators", "unsignedLong", configProfile.RootSettings.Administrators);
        UpsertArray(root, "Reserved", "unsignedLong", configProfile.RootSettings.Reserved.Select(value => value.ToString(CultureInfo.InvariantCulture)));
        UpsertArray(root, "Banned", "unsignedLong", configProfile.RootSettings.Banned.Select(value => value.ToString(CultureInfo.InvariantCulture)));
        UpsertPassword(root, configProfile.RootSettings.ServerPassword);
    }

    private static void UpsertBlockTypeLimits(XElement sessionSettings, IReadOnlyDictionary<string, int> limits)
    {
        var element = sessionSettings.Element("BlockTypeLimits");
        if (element is null)
        {
            element = new XElement("BlockTypeLimits");
            sessionSettings.Add(element);
        }

        element.RemoveNodes();
        element.Add(
            new XElement(
                "dictionary",
                limits
                    .Where(limit => !string.IsNullOrWhiteSpace(limit.Key))
                    .Select(limit =>
                        new XElement(
                            "item",
                            new XElement("Key", limit.Key),
                            new XElement("Value", Math.Clamp(limit.Value, 0, short.MaxValue).ToString(CultureInfo.InvariantCulture))))));
    }

    private static void UpsertPassword(XElement root, string password)
    {
        if (string.IsNullOrEmpty(password))
        {
            UpsertElement(root, "ServerPasswordHash", string.Empty);
            UpsertElement(root, "ServerPasswordSalt", string.Empty);
            return;
        }

        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 10000, HashAlgorithmName.SHA1, 20);
        UpsertElement(root, "ServerPasswordHash", Convert.ToBase64String(hash));
        UpsertElement(root, "ServerPasswordSalt", Convert.ToBase64String(salt));
    }

    private static string BuildLaunchArguments(
        DedicatedServerDefinition definition,
        string dedicatedServerAppDataPath,
        string magnetarAppDataPath,
        string dedicatedServer64Path,
        string worldPath,
        string runtimeConfigPath,
        WebServiceOptions options,
        bool? dataHandlingConsent)
    {
        var baseArguments = ExpandLaunchArguments(
            definition,
            dedicatedServerAppDataPath,
            magnetarAppDataPath,
            dedicatedServer64Path,
            worldPath,
            runtimeConfigPath,
            options);
        if (IgnoreLastSessionPattern.IsMatch(baseArguments))
            throw new InvalidOperationException("Launch arguments cannot include -ignorelastsession for Quasar-managed servers.");

        var sanitizedArguments = StripManagedArguments(baseArguments);
        var additions = new List<string>
        {
            "-noconsole",
            // Detach Magnetar from Quasar's session (Linux setsid / Windows FreeConsole),
            // in place so the PID and stdout/stderr pipes stay valid. This keeps managed
            // servers alive when Quasar stops and shields them from terminal-driven signals.
            "-daemon",
        };

        if (definition.DisableImplicitMagnetarModLoad)
            additions.Add("-noimplicitmod");

        additions.Add($"-path {QuoteArgument(dedicatedServerAppDataPath)}");
        additions.Add($"-config {QuoteArgument(magnetarAppDataPath)}");
        additions.Add($"-ds64 {QuoteArgument(dedicatedServer64Path)}");
        additions.Add(dataHandlingConsent == true ? "-consent" : "-noconsent");

        if (string.IsNullOrWhiteSpace(sanitizedArguments))
            return string.Join(" ", additions);

        return $"{sanitizedArguments} {string.Join(" ", additions)}";
    }

    private static string ExpandLaunchArguments(
        DedicatedServerDefinition definition,
        string dedicatedServerAppDataPath,
        string magnetarAppDataPath,
        string dedicatedServer64Path,
        string worldPath,
        string runtimeConfigPath,
        WebServiceOptions options)
    {
        return definition.LaunchArguments
            .Trim()
            .Replace("{uniqueName}", definition.UniqueName, StringComparison.OrdinalIgnoreCase)
            .Replace("{configPath}", QuoteArgument(runtimeConfigPath), StringComparison.OrdinalIgnoreCase)
            .Replace("{quasarBaseUrl}", QuoteArgument(options.BaseUrl), StringComparison.OrdinalIgnoreCase)
            .Replace("{hostId}", options.HostId, StringComparison.OrdinalIgnoreCase)
            .Replace("{dsAppDataPath}", QuoteArgument(dedicatedServerAppDataPath), StringComparison.OrdinalIgnoreCase)
            .Replace("{magnetarAppDataPath}", QuoteArgument(magnetarAppDataPath), StringComparison.OrdinalIgnoreCase)
            .Replace("{ds64Path}", QuoteArgument(dedicatedServer64Path), StringComparison.OrdinalIgnoreCase)
            .Replace("{worldPath}", QuoteArgument(worldPath), StringComparison.OrdinalIgnoreCase);
    }

    private static string StripManagedArguments(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
            return string.Empty;

        var sanitized = arguments;
        sanitized = ConsolePattern.Replace(sanitized, string.Empty);
        sanitized = NoConsolePattern.Replace(sanitized, string.Empty);
        sanitized = PathOptionPattern.Replace(sanitized, string.Empty);
        sanitized = ConfigOptionPattern.Replace(sanitized, string.Empty);
        sanitized = Ds64OptionPattern.Replace(sanitized, string.Empty);
        sanitized = NoSplashPattern.Replace(sanitized, string.Empty);
        sanitized = DaemonPattern.Replace(sanitized, string.Empty);
        sanitized = NoImplicitModPattern.Replace(sanitized, string.Empty);
        sanitized = ConsentOptionPattern.Replace(sanitized, string.Empty);
        sanitized = Regex.Replace(sanitized, @"\s{2,}", " ");
        return sanitized.Trim();
    }

    private async Task<string> ResolveOrSeedWorldPathAsync(
        DedicatedServerDefinition definition,
        CancellationToken cancellationToken)
    {
        var worldPath = RequirePath(definition.WorldPath, "WorldPath");

        // World already exists — validate and use it.
        if (Directory.Exists(worldPath) && File.Exists(Path.Combine(worldPath, "Sandbox.sbc")))
            return ResolveWorldPath(worldPath);

        // World doesn't exist yet — seed from template if one is set.
        if (!string.IsNullOrWhiteSpace(definition.WorldTemplateId))
        {
            var template = _worldTemplates.GetTemplate(definition.WorldTemplateId)
                ?? throw new InvalidOperationException($"Unknown world template '{definition.WorldTemplateId}' for server '{definition.UniqueName}'.");

            await SeedWorldFromTemplateAsync(definition.WorldTemplateId, worldPath, cancellationToken);
            _logger.LogInformation(
                "Seeded world for server {UniqueName} from template '{TemplateName}' at {WorldPath}.",
                definition.UniqueName, template.Name, worldPath);
            return worldPath;
        }

        // No template — fall through to standard validation (throws if missing).
        return ResolveWorldPath(worldPath);
    }

    private async Task SeedWorldFromTemplateAsync(
        string worldTemplateId,
        string destWorldPath,
        CancellationToken cancellationToken)
    {
        var sourceDir = _worldTemplates.GetWorldDirectory(worldTemplateId);

        if (!Directory.Exists(sourceDir))
            throw new InvalidOperationException($"World template '{worldTemplateId}' has no stored world files at '{sourceDir}'.");

        if (!File.Exists(Path.Combine(sourceDir, "Sandbox.sbc")))
            throw new InvalidOperationException($"World template '{worldTemplateId}' is missing Sandbox.sbc.");

        Directory.CreateDirectory(destWorldPath);
        foreach (var sourceFile in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(sourceDir, sourceFile);
            var destFile = Path.Combine(destWorldPath, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
            File.Copy(sourceFile, destFile, overwrite: false);
        }
    }

    private static string ResolveWorldPath(string worldPath)
    {
        var path = RequirePath(worldPath, "WorldPath");
        if (File.Exists(path))
        {
            if (string.Equals(Path.GetFileName(path), "Sandbox.sbc", StringComparison.OrdinalIgnoreCase))
                path = Path.GetDirectoryName(path) ?? throw new InvalidOperationException($"Invalid world path '{worldPath}'.");
            else
                throw new InvalidOperationException($"World path '{worldPath}' points to a file instead of a world directory.");
        }

        if (!Directory.Exists(path))
            throw new InvalidOperationException($"World directory not found: {path}");

        var checkpointPath = Path.Combine(path, "Sandbox.sbc");
        if (!File.Exists(checkpointPath))
            throw new InvalidOperationException($"World directory '{path}' does not contain Sandbox.sbc.");

        return path;
    }

    private static string RequirePath(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"{name} is not configured.");

        return value.Trim();
    }

    private static string GetWorldDisplayName(DedicatedServerDefinition definition, string worldPath)
    {
        if (!string.IsNullOrWhiteSpace(definition.InGameWorldName))
            return definition.InGameWorldName.Trim();

        if (!string.IsNullOrWhiteSpace(definition.UniqueName))
            return definition.UniqueName.Trim();

        return Path.GetFileName(worldPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }

    private static string GetServerDisplayName(DedicatedServerDefinition definition)
    {
        if (!string.IsNullOrWhiteSpace(definition.InGameServerName))
            return definition.InGameServerName.Trim();

        if (!string.IsNullOrWhiteSpace(definition.DisplayName))
            return definition.DisplayName.Trim();

        return definition.UniqueName.Trim();
    }

    private static string? TryGetRelativePath(string rootPath, string fullPath)
    {
        try
        {
            var relativePath = Path.GetRelativePath(rootPath, fullPath);
            if (relativePath.StartsWith("..", StringComparison.Ordinal))
                return null;

            return relativePath;
        }
        catch
        {
            return null;
        }
    }

    private static string SerializeXml(XDocument document)
    {
        using var stringWriter = new Utf8StringWriter();
        using var xmlWriter = XmlWriter.Create(stringWriter, new XmlWriterSettings
        {
            Indent = true,
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            OmitXmlDeclaration = false,
            NewLineChars = "\n",
            NewLineHandling = NewLineHandling.Replace,
        });

        document.Save(xmlWriter);
        xmlWriter.Flush();
        return stringWriter.ToString();
    }

    private static void UpsertElement(XElement root, string name, string value)
    {
        var element = root.Element(name);
        if (element is null)
        {
            root.Add(new XElement(name, value));
            return;
        }

        element.Value = value;
    }

    private static void UpsertArray(XElement root, string name, string itemName, IEnumerable<string> values)
    {
        var element = root.Element(name);
        if (element is null)
        {
            element = new XElement(name);
            root.Add(element);
        }
        else
        {
            element.RemoveNodes();
        }

        foreach (var value in values.Where(value => !string.IsNullOrWhiteSpace(value)))
            element.Add(new XElement(itemName, value));
    }

    private static string QuoteArgument(string value) => $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    private sealed class Utf8StringWriter : StringWriter
    {
        public override Encoding Encoding => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    }
}

public sealed record PreparedDedicatedServerLaunch(
    string DedicatedServerAppDataPath,
    string MagnetarAppDataPath,
    string DedicatedServer64Path,
    string WorldPath,
    string RuntimeConfigPath,
    string LastSessionPath,
    string Arguments);

public sealed record AgentDeploymentComparison(
    string BundledPath,
    string BundledSha256,
    string DeployedPath,
    string DeployedSha256)
{
    public bool CanCompare =>
        !string.IsNullOrWhiteSpace(BundledSha256) &&
        !string.IsNullOrWhiteSpace(DeployedSha256);

    public bool HasMismatch =>
        !string.IsNullOrWhiteSpace(BundledSha256) &&
        !string.Equals(BundledSha256, DeployedSha256, StringComparison.OrdinalIgnoreCase);
}
