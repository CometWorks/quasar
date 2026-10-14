using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Xml.Linq;
using Magnetar.Protocol.Runtime;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Quasar.Models;
using Quasar.Services;
using Quasar.Services.Plugins;
using Xunit;

namespace Quasar.Tests;

[Collection("Exact server creation")]
public sealed class PluginHubTests : IDisposable
{
    private const string MagnetarHubUrl =
        "https://github.com/CometWorks/magnetar-hub/archive/refs/heads/main.zip";
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "quasar-plugin-hubs-" + Guid.NewGuid().ToString("N")
    );
    private readonly FieldInfo _cache = typeof(MagnetarPaths).GetField(
        "_cachedQuasarDirectory",
        BindingFlags.NonPublic | BindingFlags.Static
    )!;
    private readonly object? _previousRoot;
    private readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> _routes =
        new();
    private readonly List<HttpRequestMessage> _requests = [];

    public PluginHubTests()
    {
        _previousRoot = _cache.GetValue(null);
        _cache.SetValue(null, _root);
        Directory.CreateDirectory(_root);
        _routes[MagnetarHubUrl] = _ =>
            Zip(
                "magnetar-hub-main",
                ("Plugins/Essentials.xml", Manifest("essentials", "CometWorks/essentials")),
                ("Plugins/LinuxCompat.xml", Manifest("linux-compat", "CometWorks/linux-compat")),
                ("SamplePlugin.xml", Manifest("sample", "CometWorks/sample"))
            );
    }

    [Fact]
    public async Task CatalogListsEveryHubAndNamesTheHubsOfADuplicateId()
    {
        string folder = LocalHub(
            ("Worlds/Octahedron.xml", Manifest("octahedron", "CometWorks/octahedron")),
            ("Essentials.xml", Manifest("essentials", "Someone/essentials-fork")),
            ("NotAPlugin.xml", "<Project />")
        );
        _routes["https://github.com/Owner/private-hub/archive/refs/heads/dev.zip"] = _ =>
            Zip("private-hub-dev", ("Custom.xml", Manifest("custom", "Owner/custom")));
        var hubs = new List<QuasarPluginHub>
        {
            new()
            {
                Name = "Private",
                Repo = "Owner/private-hub",
                Branch = "dev",
            },
            new() { Name = "Local", Folder = folder },
        };
        var catalog = CreateCatalog(() => hubs);

        await catalog.RefreshAsync();

        var entries = catalog.GetHubEntries().ToDictionary(entry => entry.PluginId);
        Assert.Equal(["custom", "essentials", "linux-compat", "octahedron"], entries.Keys.Order());
        Assert.Equal("Private", entries["custom"].HubName);
        Assert.Equal("Owner/private-hub", entries["custom"].ManifestRepo);
        Assert.Equal("Custom.xml", entries["custom"].ManifestFile);
        Assert.Equal("Local", entries["octahedron"].HubName);
        Assert.Equal("Worlds/Octahedron.xml", entries["octahedron"].ManifestFile);
        // MagnetarHub is read first and keeps the entry, but the conflict is reported, not resolved.
        Assert.Equal("CometWorks/essentials", entries["essentials"].SourceRepo);
        Assert.Equal(["MagnetarHub", "Local"], entries["essentials"].ConflictingHubs);
        Assert.Empty(entries["custom"].ConflictingHubs);
        Assert.Equal(string.Empty, catalog.LastError);

        hubs.RemoveAt(1);
        entries = catalog.GetHubEntries().ToDictionary(entry => entry.PluginId);
        Assert.False(entries.ContainsKey("octahedron"));
        Assert.Empty(entries["essentials"].ConflictingHubs);
    }

    [Fact]
    public async Task UnreachableHubKeepsItsCachedPluginsAndTheOthersRefresh()
    {
        string cachePath = Path.Combine(_root, "catalog.json");
        bool online = true;
        _routes["https://github.com/Owner/hub/archive/refs/heads/main.zip"] = _ =>
            online
                ? Zip("hub-main", ("Custom.xml", Manifest("custom", "Owner/custom")))
                : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        QuasarPluginHub[] hubs =
        [
            new()
            {
                Name = "Owner hub",
                Repo = "Owner/hub",
                Branch = "main",
            },
        ];
        await CreateCatalog(() => hubs, cachePath).RefreshAsync();

        online = false;
        var catalog = CreateCatalog(() => hubs, cachePath);
        await catalog.RefreshAsync();

        var custom = Assert.Single(catalog.GetHubEntries(), entry => entry.PluginId == "custom");
        Assert.Contains("Owner hub", custom.HubError);
        Assert.Empty(
            Assert.Single(catalog.GetHubEntries(), entry => entry.PluginId == "essentials").HubError
        );
        var status = Assert.Single(catalog.GetHubStatuses(), item => !item.IsDefault);
        Assert.Equal(1, status.PluginCount);
        Assert.NotEmpty(status.Error);
        Assert.Contains("Owner hub", catalog.LastError);
    }

    [Fact]
    public async Task PrivateHubDownloadsThroughTheApiWithTheTokenAndMagnetarHubStaysAnonymous()
    {
        _routes["https://api.github.com/repos/Owner/private/zipball/main"] = request =>
        {
            Assert.Equal("Bearer secret", request.Headers.Authorization?.ToString());
            return Zip(
                "Owner-private-0123abc",
                ("Plugins/Custom.xml", Manifest("custom", "Owner/custom"))
            );
        };
        var catalog = CreateCatalog(
            () =>
                [
                    new()
                    {
                        Name = "Private",
                        Repo = "Owner/private",
                        Branch = "main",
                    },
                ],
            token: "secret"
        );

        await catalog.RefreshAsync();

        Assert.Equal(
            "Plugins/Custom.xml",
            Assert.Single(catalog.GetHubEntries(), entry => entry.PluginId == "custom").ManifestFile
        );
        Assert.Null(
            Assert
                .Single(_requests, request => request.RequestUri!.ToString() == MagnetarHubUrl)
                .Headers.Authorization
        );
    }

    [Fact]
    public async Task HubRegistryRejectsDuplicatesAndInvalidSources()
    {
        var registry = new QuasarPluginHubCatalog(NullLogger<QuasarPluginHubCatalog>.Instance);
        await registry.AddAsync(new() { Repo = " Owner/hub/ " });
        var hub = Assert.Single(registry.GetHubs());
        Assert.Equal(("Owner/hub", "main", "Owner/hub"), (hub.Repo, hub.Branch, hub.Name));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.AddAsync(new() { Repo = "owner/HUB", Branch = "dev" })
        );
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.AddAsync(new() { Repo = "CometWorks/magnetar-hub" })
        );
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.AddAsync(new() { Repo = "not a repo" })
        );
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.AddAsync(new() { Repo = "Other/hub", Name = "MagnetarHub" })
        );
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.AddAsync(new() { Folder = "relative/hub" })
        );
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.AddAsync(new() { Folder = Path.Combine(_root, "missing") })
        );

        string folder = LocalHub();
        await registry.AddAsync(new() { Folder = folder + "/" });
        Assert.Equal(
            folder,
            new QuasarPluginHubCatalog(NullLogger<QuasarPluginHubCatalog>.Instance)
                .GetHubs()[1]
                .Folder
        );

        await registry.RemoveAsync("OWNER/hub");
        Assert.Equal(folder, Assert.Single(registry.GetHubs()).Key);
    }

    [Fact]
    public async Task PreparerWritesEveryHubAndSelectsTheirPluginsById()
    {
        string folder = LocalHub(
            ("Octahedron.xml", Manifest("octahedron", "CometWorks/octahedron"))
        );
        _routes["https://github.com/Owner/hub/archive/refs/heads/main.zip"] = _ =>
            Zip("hub-main", ("Custom.xml", Manifest("custom", "Owner/custom")));
        var registry = new QuasarPluginHubCatalog(NullLogger<QuasarPluginHubCatalog>.Instance);
        await registry.AddAsync(new() { Name = "Owner hub", Repo = "Owner/hub" });
        await registry.AddAsync(new() { Name = "Worlds", Folder = folder });
        var (preparer, catalog) = CreatePreparer(registry.GetHubs);
        await catalog.RefreshAsync();
        var profile = new QuasarConfigProfile
        {
            Plugins =
            [
                new() { PluginId = "custom" },
                new() { PluginId = "octahedron" },
                new() { PluginId = "essentials" },
            ],
        };

        var prepared = await PrepareAsync(preparer, profile);

        var sources = XDocument
            .Load(Path.Combine(prepared.MagnetarAppDataPath, "Sources/sources.xml"))
            .Root!;
        var local = Assert.Single(sources.Element("LocalHubSources")!.Elements("LocalHub"));
        Assert.Equal(
            ("Worlds", folder, "true"),
            (
                local.Element("Name")?.Value,
                local.Element("Folder")?.Value,
                local.Element("Enabled")?.Value
            )
        );
        Assert.Equal(
            ["CometWorks/magnetar-hub", "Owner/hub"],
            sources
                .Element("RemoteHubSources")!
                .Elements("RemoteHub")
                .Select(hub => hub.Element("Repo")!.Value)
        );
        // Only MagnetarHub plugins keep their per-manifest source; a registered hub is always a source.
        Assert.Equal(
            ["Plugins/Essentials.xml"],
            sources
                .Element("RemotePluginSources")!
                .Elements("RemotePlugin")
                .Select(plugin => plugin.Element("File")!.Value)
        );
        var current = XDocument
            .Load(Path.Combine(prepared.MagnetarAppDataPath, "Profiles/Current.xml"))
            .Root!;
        Assert.Equal(
            ["custom", "essentials", "octahedron"],
            current
                .Element("GitHub")!
                .Elements("GitHubPluginConfig")
                .Select(plugin => plugin.Element("Id")!.Value)
                .Order()
        );
        Assert.Empty(prepared.UnresolvedPluginIds ?? []);
    }

    [Fact]
    public async Task PreparerRefusesAPluginTwoHubsListAndReportsUnlistedOnes()
    {
        string folder = LocalHub(
            ("Essentials.xml", Manifest("essentials", "Someone/essentials-fork"))
        );
        var (preparer, catalog) = CreatePreparer(() => [new() { Name = "Forks", Folder = folder }]);
        await catalog.RefreshAsync();

        var conflict = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PrepareAsync(
                preparer,
                new QuasarConfigProfile { Plugins = [new() { PluginId = "essentials" }] }
            )
        );
        Assert.Contains("essentials (MagnetarHub, Forks)", conflict.Message);

        var prepared = await PrepareAsync(
            preparer,
            new QuasarConfigProfile { Plugins = [new() { PluginId = "unknown" }] }
        );
        Assert.Equal(["unknown"], prepared.UnresolvedPluginIds);
    }

    [Fact]
    public async Task PreparerRefusesACoreCompatibilityPluginAnotherHubAlsoLists()
    {
        string folder = LocalHub(
            ("LinuxCompat.xml", Manifest("linux-compat", "Someone/linux-compat"))
        );
        var (preparer, catalog) = CreatePreparer(() => [new() { Name = "Forks", Folder = folder }]);
        await catalog.RefreshAsync();

        var conflict = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PrepareAsync(preparer, new QuasarConfigProfile())
        );
        Assert.Contains("linux-compat (MagnetarHub, Forks)", conflict.Message);
    }

    [Fact]
    public void ContentMonitorReportsConflictsAndUnreachableHubsInsteadOfAVersion()
    {
        var pin = new ClusterPluginPin("custom", "Custom", "Owner/custom", new('a', 40));
        var entry = new QuasarPluginCatalogEntry
        {
            PluginId = "custom",
            SourceRepo = "Owner/custom",
            SourceCommit = new('b', 40),
            HubName = "Owner hub",
        };
        var prior = new ClusterContentItem(
            "plugin",
            "custom",
            "Custom",
            pin.Commit,
            null,
            new string('c', 40),
            DateTimeOffset.UnixEpoch,
            null,
            null
        );

        entry.HubError = "Owner hub: 503";
        var offline = ClusterContentMonitor.ObservePlugin(
            pin,
            entry,
            prior,
            null,
            DateTimeOffset.UtcNow
        );
        Assert.Contains("Owner hub", offline.Error);
        Assert.Equal(prior.LatestVersion, offline.LatestVersion);

        entry.HubError = string.Empty;
        entry.ConflictingHubs = ["Owner hub", "Forks"];
        Assert.Contains(
            "Owner hub, Forks",
            ClusterContentMonitor.ObservePlugin(pin, entry, null, null, DateTimeOffset.UtcNow).Error
        );
    }

    private QuasarPluginCatalogService CreateCatalog(
        Func<IReadOnlyList<QuasarPluginHub>> hubs,
        string? cachePath = null,
        string? token = null
    ) =>
        new(
            NullLogger<QuasarPluginCatalogService>.Instance,
            new Factory(new Handler(Send)),
            new QuasarDevFolderCatalog(NullLogger<QuasarDevFolderCatalog>.Instance),
            cachePath ?? Path.Combine(_root, "catalog-" + Guid.NewGuid().ToString("N") + ".json"),
            hubs,
            () => token
        );

    private (
        DedicatedServerRuntimePreparer Preparer,
        QuasarPluginCatalogService Catalog
    ) CreatePreparer(Func<IReadOnlyList<QuasarPluginHub>> hubs)
    {
        var devFolders = new QuasarDevFolderCatalog(NullLogger<QuasarDevFolderCatalog>.Instance);
        var profiles = new QuasarConfigProfileCatalog(
            NullLogger<QuasarConfigProfileCatalog>.Instance
        );
        var consent = new DataHandlingConsentCatalog(
            NullLogger<DataHandlingConsentCatalog>.Instance
        );
        var credentials = new GitHubUpdateCredentialsCatalog(
            NullLogger<GitHubUpdateCredentialsCatalog>.Instance,
            new EphemeralDataProtectionProvider()
        );
        _disposables.AddRange([profiles, consent, credentials]);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?> { ["Quasar:Plugins:SafeMode"] = "true" }
            )
            .Build();
        var uiPlugins = QuasarUiPluginCatalog.Create(
            configuration,
            new HostingEnvironment { ContentRootPath = _root }
        );
        var catalog = new QuasarPluginCatalogService(
            NullLogger<QuasarPluginCatalogService>.Instance,
            new Factory(new Handler(Send)),
            devFolders,
            Path.Combine(_root, "catalog.json"),
            hubs
        );
        return (
            new DedicatedServerRuntimePreparer(
                NullLogger<DedicatedServerRuntimePreparer>.Instance,
                new WebServiceOptions(),
                consent,
                profiles,
                catalog,
                devFolders,
                uiPlugins,
                credentials
            ),
            catalog
        );
    }

    private readonly List<IDisposable> _disposables = [];

    private async Task<PreparedDedicatedServerLaunch> PrepareAsync(
        DedicatedServerRuntimePreparer preparer,
        QuasarConfigProfile profile
    )
    {
        var definition = new DedicatedServerDefinition
        {
            UniqueName = "hub-test",
            WorldSaveName = "World",
        };
        Directory.CreateDirectory(definition.GetWorldSavePath());
        File.WriteAllText(
            Path.Combine(definition.GetWorldSavePath(), "Sandbox.sbc"),
            "<MyObjectBuilder_Checkpoint />"
        );
        return await preparer.PrepareAsync(
            definition,
            Path.Combine(_root, "DedicatedServer64"),
            MagnetarLaunchArgumentStyle.Current,
            profileOverride: profile
        );
    }

    private HttpResponseMessage Send(HttpRequestMessage request)
    {
        _requests.Add(request);
        return _routes.TryGetValue(request.RequestUri!.ToString(), out var route)
            ? route(request)
            : new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private string LocalHub(params (string Path, string Content)[] files)
    {
        string folder = Path.Combine(_root, "hub-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        foreach (var (path, content) in files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(folder, path))!);
            File.WriteAllText(Path.Combine(folder, path), content);
        }
        return folder;
    }

    private static string Manifest(string id, string repo) =>
        $"<PluginData xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xsi:type=\"GitHubPlugin\"><Id>{id}</Id><FriendlyName>{id}</FriendlyName>"
        + $"<RepoId>{repo}</RepoId><Commit>{new string('a', 40)}</Commit></PluginData>";

    private static HttpResponseMessage Zip(string top, params (string Path, string Content)[] files)
    {
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
        {
            foreach (var (path, content) in files)
            {
                using var writer = new StreamWriter(zip.CreateEntry(top + "/" + path).Open());
                writer.Write(content);
            }
        }
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes.ToArray()),
        };
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken token
        ) => Task.FromResult(send(request));
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    public void Dispose()
    {
        foreach (var disposable in _disposables)
            disposable.Dispose();
        _cache.SetValue(null, _previousRoot);
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }
}
