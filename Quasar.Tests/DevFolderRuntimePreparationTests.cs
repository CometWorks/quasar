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
public sealed class DevFolderRuntimePreparationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "quasar-dev-folder-" + Guid.NewGuid().ToString("N"));
    private readonly FieldInfo _cache = typeof(MagnetarPaths).GetField("_cachedQuasarDirectory", BindingFlags.NonPublic | BindingFlags.Static)!;
    private readonly object? _previousRoot;

    public DevFolderRuntimePreparationTests()
    {
        _previousRoot = _cache.GetValue(null);
        _cache.SetValue(null, _root);
    }

    [Theory]
    [InlineData("914a8b69-5d79-4b80-90ba-9ccf0a1c4827", false)]
    [InlineData(null, true)]
    public async Task ManifestIsPassedToSourceAndItsIdentityEnablesTheSelectedPlugin(string? manifestId, bool debugBuild)
    {
        string folder = Path.Combine(_root, "multi-project-repo");
        string manifest = Path.Combine(folder, "Manifests", "Plugin.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        var manifestDocument = new XDocument(new XElement("PluginData",
            new XAttribute(XNamespace.Xmlns + "xsi", "http://www.w3.org/2001/XMLSchema-instance"),
            new XAttribute(XName.Get("type", "http://www.w3.org/2001/XMLSchema-instance"), "GitHubPlugin"),
            manifestId is null ? null : new XElement("Id", manifestId),
            new XElement("SourceDirectories", new XElement("Directory", "Server")),
            new XElement("NuGetReferences", new XElement("PackageReference", new XAttribute("Include", "Newtonsoft.Json"), new XAttribute("Version", "13.0.3")))));
        manifestDocument.Save(manifest);
        string originalManifest = File.ReadAllText(manifest);

        var devFolders = new QuasarDevFolderCatalog(NullLogger<QuasarDevFolderCatalog>.Instance);
        await devFolders.UpsertAsync(new() { FolderPath = folder, DataFile = "Manifests/Plugin.xml", PluginId = "multi-project-repo", DebugBuild = debugBuild });
        using var profiles = new QuasarConfigProfileCatalog(NullLogger<QuasarConfigProfileCatalog>.Instance);
        using var consent = new DataHandlingConsentCatalog(NullLogger<DataHandlingConsentCatalog>.Instance);
        using var credentials = new GitHubUpdateCredentialsCatalog(NullLogger<GitHubUpdateCredentialsCatalog>.Instance, new EphemeralDataProtectionProvider());
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Quasar:Plugins:SafeMode"] = "true" }).Build();
        var uiPlugins = QuasarUiPluginCatalog.Create(configuration, new HostingEnvironment { ContentRootPath = _root });
        var catalog = new QuasarPluginCatalogService(NullLogger<QuasarPluginCatalogService>.Instance, new NoNetwork(), devFolders);
        var preparer = new DedicatedServerRuntimePreparer(NullLogger<DedicatedServerRuntimePreparer>.Instance,
            new WebServiceOptions(), consent, profiles, catalog, devFolders, uiPlugins, credentials);
        var profile = new QuasarConfigProfile { Plugins = [new() { PluginId = "multi-project-repo" }] };

        var definition = new DedicatedServerDefinition { UniqueName = "dev-folder-test", WorldSaveName = "World" };
        Directory.CreateDirectory(definition.GetWorldSavePath());
        File.WriteAllText(Path.Combine(definition.GetWorldSavePath(), "Sandbox.sbc"), "<MyObjectBuilder_Checkpoint />");
        var prepared = await preparer.PrepareAsync(definition,
            Path.Combine(_root, "DedicatedServer64"), MagnetarLaunchArgumentStyle.Current, profileOverride: profile);

        var source = Assert.Single(XDocument.Load(Path.Combine(prepared.MagnetarAppDataPath, "Sources/sources.xml"))
            .Root!.Element("LocalPluginSources")!.Elements("LocalPlugin"));
        Assert.Equal("Manifests/Plugin.xml", source.Element("File")?.Value);
        Assert.Equal(folder, source.Element("Folder")?.Value);
        Assert.Equal("true", source.Element("Enabled")?.Value);
        var current = XDocument.Load(Path.Combine(prepared.MagnetarAppDataPath, "Profiles/Current.xml")).Root!;
        var selected = Assert.Single(current.Element("DevFolder")!.Elements("LocalFolderConfig"));
        Assert.Equal(manifestId ?? "multi-project-repo", selected.Element("Id")?.Value);
        Assert.Equal(debugBuild ? "true" : "false", selected.Element("DebugBuild")?.Value);
        Assert.Null(selected.Element("DataFile"));
        Assert.Empty(current.Element("GitHub")!.Elements());
        Assert.Equal(originalManifest, File.ReadAllText(manifest));
        Assert.Equal("multi-project-repo", Assert.Single(profile.Plugins).PluginId);
    }

    private sealed class NoNetwork : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("Runtime preparation must not access the network for a dev-folder selection.");
    }

    public void Dispose()
    {
        _cache.SetValue(null, _previousRoot);
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
