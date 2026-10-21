using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Quasar.Models;
using Quasar.Services;
using Quasar.Services.Auth;
using Xunit;

namespace Quasar.Tests;

public sealed class ClusterSetupTests
{
    private static EnrolledClusterHost Host(string id, string address) => new(id, id, address, 18400, "credential");
    private static ClusterSetupRequest Request(params ClusterSetupPlacement[] machines) => new("demo", "Demo", "world", "profile", "one", 28000, machines);
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(33)]
    public void EverySelectedHostRequiresOneToThirtyTwoRegularNodes(int nodes)
    {
        Assert.Throws<ArgumentException>(() => ClusterSetupService.Validate(
            Request(new("one", 2), new("two", nodes)), [Host("one", "10.1.0.1"), Host("two", "10.1.0.2")]));

        var cluster = new ClusterDefinition { GatewayUrl = "http://10.1.0.1:28016", GatewayAdminTokenEnvironmentVariable = "ADMIN" };
        var conversion = new ServerToClusterRequest(Guid.NewGuid(), "server", "revision", "1210014", "one", 28000,
            "JOIN", "TOKENS", ["10.1.0.0/24"],
            [new("one", "http://one:18400", "HOST_ONE", "EXEC_ONE", "10.1.0.1", 2),
             new("two", "http://two:18400", "HOST_TWO", "EXEC_TWO", "10.1.0.2", nodes)]);
        Assert.Throws<InvalidDataException>(() => ClusterConversionService.ValidateTopology(cluster, conversion));
    }

    [Fact]
    public void HotSparesAreOneCountPerClusterThatFitsNextToTheRegularNodes()
    {
        Assert.Single(ClusterSetupService.Validate(Request(new ClusterSetupPlacement("one", 2)) with { HotSpares = 1 }, [Host("one", "127.0.0.1")]));
        Assert.Single(ClusterSetupService.Validate(Request(new ClusterSetupPlacement("one", 31)) with { HotSpares = 1 }, [Host("one", "127.0.0.1")]));
        foreach (int spares in new[] { -1, 2 })
            Assert.Throws<ArgumentException>(() => ClusterSetupService.Validate(Request(new ClusterSetupPlacement("one", 31)) with { HotSpares = spares }, [Host("one", "127.0.0.1")]));

        var cluster = new ClusterDefinition { GatewayUrl = "http://10.1.0.1:28016", GatewayAdminTokenEnvironmentVariable = "ADMIN" };
        var conversion = new ServerToClusterRequest(Guid.NewGuid(), "server", "revision", "1210014", "two", 28000,
            "JOIN", "TOKENS", ["10.1.0.0/24"], [new("one", "http://one:18400", "HOST_ONE", "EXEC_ONE", "10.1.0.1", 2),
                new("two", "http://two:18400", "HOST_TWO", "EXEC_TWO", "10.1.0.2", 31)], HotSpares: 3);
        ClusterConversionService.ValidateTopology(cluster, conversion);
        // From the Gateway host on, one at a time, never above 32 nodes on a host.
        Assert.Equal(new Dictionary<string, int> { ["one"] = 2, ["two"] = 1 }, ClusterConversionService.PlaceHotSpares(conversion));
        Assert.Throws<InvalidDataException>(() => ClusterConversionService.ValidateTopology(cluster, conversion with { HotSpares = 32 }));
        Assert.Throws<InvalidDataException>(() => ClusterConversionService.ValidateTopology(cluster, conversion with { HotSpares = -1 }));
    }

    [Fact]
    public void PlacementSupportsBothLocalAndRemoteButRefusesMixedLoopback()
    {
        Assert.Single(ClusterSetupService.Validate(Request(new ClusterSetupPlacement("one", 2)), [Host("one", "127.0.0.1")]));
        Assert.Equal(2, ClusterSetupService.Validate(Request(new ClusterSetupPlacement("one", 1), new("two", 1)), [Host("one", "10.1.0.1"), Host("two", "10.1.0.2")]).Length);
        Assert.Throws<ArgumentException>(() => ClusterSetupService.Validate(Request(new ClusterSetupPlacement("one", 1), new("two", 1)), [Host("one", "127.0.0.1"), Host("two", "10.1.0.2")]));
        Assert.Throws<ArgumentException>(() => ClusterSetupService.Validate(Request(new ClusterSetupPlacement("one", 1)), [Host("one", "10.1.0.1")]));
        Assert.Throws<ArgumentException>(() => ClusterSetupService.Validate(Request(new ClusterSetupPlacement("one", 2), new("one", 2)), [Host("one", "10.1.0.1")]));
        Assert.Throws<ArgumentException>(() => ClusterSetupService.Validate(Request(new ClusterSetupPlacement("missing", 2)), [Host("one", "10.1.0.1")]));
    }
    [Theory]
    [InlineData("10.1.0.1", true)]
    [InlineData("172.16.1.1", true)]
    [InlineData("172.32.1.1", false)]
    [InlineData("192.168.1.1", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("fd00::1", true)]
    [InlineData("0.0.0.0", false)]
    [InlineData("::", false)]
    [InlineData("8.8.8.8", false)]
    public void ClusterControlAddressesStayOnPrivateNetworks(string address, bool allowed) =>
        Assert.Equal(allowed, ClusterHostCatalog.IsClusterAddress(address));

    [Fact]
    public void SteamDefaultLeavesTheDirectTransportDefaultFree()
    {
        // Steam 27016 next to Direct Transport 27116 (#1242): the nodes moved from P+100 to P+1100.
        Assert.Equal(28116, ClusterSetupService.NodePortBase(27016));
        Assert.Equal(new[] { (27016, 27032), (28116, 28280) }, ClusterSetupService.ReservedPorts(27016));
        Assert.False(ClusterSetupService.IsReserved(27016, 27116));
        foreach (int port in new[] { 27016, 27032, 28116, 28180, 28216, 28280 })
            Assert.True(ClusterSetupService.IsReserved(27016, port));
        Assert.False(ClusterSetupService.IsReserved(27016, 28281));
        // The highest player port still gets its whole node range below 65536.
        Assert.Single(ClusterSetupService.Validate(Request(new ClusterSetupPlacement("one", 2)) with { PlayerPort = 64000 }, [Host("one", "127.0.0.1")]));
        Assert.Throws<ArgumentException>(() => ClusterSetupService.Validate(Request(new ClusterSetupPlacement("one", 2)) with { PlayerPort = 64001 }, [Host("one", "127.0.0.1")]));
    }

    [Fact]
    public async Task GuidedPortsAndPerHostRuntimePathsReachTheShippedSpecification()
    {
        var cluster = new ClusterDefinition { UniqueName = "demo", GatewayUrl = "http://10.1.0.1:27032", GatewayAdminTokenEnvironmentVariable = "ADMIN" };
        var request = new ServerToClusterRequest(Guid.NewGuid(), "", "", "1210014", "one", 27016, "JOIN", "TOKENS",
            ["10.1.0.1/32", "10.1.0.2/32"], [new("one", "http://one.quasar-host.invalid:18400", "HOST_ONE", "EXEC_ONE", "10.1.0.1", 1),
                new("two", "http://two.quasar-host.invalid:18400", "HOST_TWO", "EXEC_TWO", "10.1.0.2", 1)], NodePortBase: ClusterSetupService.NodePortBase(27016));
        string world = Path.Combine(Path.GetTempPath(), "cluster-setup-world-" + Guid.NewGuid()); Directory.CreateDirectory(world);
        try
        {
            File.WriteAllText(Path.Combine(world, "Sandbox.sbc"), "world");
            // Test the actual specification builder, not a second copy of its port formula.
            var paths = new Dictionary<string, Quasar.Host.Contract.V1.HostConversionPaths>();
            foreach (string host in new[] { "one", "two" })
                paths[host] = JsonSerializer.Deserialize<Quasar.Host.Contract.V1.HostConversionPaths>(
                    $$"""{"hostId":"{{host}}","directory":"/world","configurationDirectory":"/config","runtimeDirectory":"/runs/{{host}}"}""", new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            using var spec = JsonDocument.Parse(await ClusterConversionService.SpecificationAsync(cluster, new() { DisplayName = "Demo" }, new(), null, request, world, paths, default));
            var nodes = spec.RootElement.GetProperty("nodes").EnumerateArray().ToArray();
            Assert.Equal(3, nodes.Length);
            Assert.Contains(nodes, n => n.GetProperty("role").GetString() == "WA" && n.GetProperty("backend").GetString() == "10.1.0.1:28180"
                && n.GetProperty("control").GetString() == "10.1.0.1:28280");
            Assert.Contains(nodes, n => n.GetProperty("host").GetString() == "two" && n.GetProperty("backend").GetString() == "10.1.0.2:28116"
                && n.GetProperty("control").GetString() == "10.1.0.2:28216");
            // Every generated port is one the reserved-port check guards, and none is the Direct Transport default.
            var ports = nodes.SelectMany(n => new[] { n.GetProperty("backend").GetString()!, n.GetProperty("control").GetString()! })
                .Append(spec.RootElement.GetProperty("gatewayControl").GetString()!).Select(e => IPEndPoint.Parse(e).Port).ToArray();
            Assert.All(ports, port => Assert.True(ClusterSetupService.IsReserved(27016, port), port.ToString()));
            Assert.DoesNotContain(27116, ports);
            Assert.Equal("0.0.0.0:27016", spec.RootElement.GetProperty("steamListen").GetString());
            Assert.Equal("/runs/two", spec.RootElement.GetProperty("hosts")[1].GetProperty("runRoot").GetString());
            Assert.False(spec.RootElement.TryGetProperty("sharedStorageRoot", out _));
        }
        finally { Directory.Delete(world, true); }
    }

    [Fact]
    public async Task GuidedSetupGivesTheGeneratorAVerifiedPayloadForEveryProfileMod()
    {
        string work = Path.Combine(Path.GetTempPath(), "cluster-setup-mods-" + Guid.NewGuid());
        string payload = Path.Combine(work, "mod-stage/mods");
        Directory.CreateDirectory(Path.Combine(payload, "123456.sbm/Data/Scripts"));
        File.WriteAllText(Path.Combine(payload, "123456.sbm/Data/Scripts/Mod.cs"), "// mod");
        try
        {
            var profile = new QuasarConfigProfile { Mods = [new() { WorkshopId = 123456, DisplayName = "Test mod" }] };
            var files = (await Quasar.ClusterDeployment.ClusterDeploymentFiles.InspectAsync(payload, default))
                .ToDictionary(p => p.Key, p => p.Value.Sha256, StringComparer.Ordinal);
            var staged = new ClusterWorkshopModStager.StagedProfileMods(profile, payload, files, new() { ["123456"] = 42 });
            var specification = new System.Text.Json.Nodes.JsonObject();
            ClusterWorkshopModStager.AddToSpecification(specification, staged, await ClusterWorkshopModStager.ProfileSettingsXmlAsync(
                Path.Combine(work, "mod-stage/profile-patch"), profile, "Demo", default));

            // managed_deployment.py prepare: the patched checkpoint lists exactly the staged <id>.sbm folders,
            // and the payload the Host receives hashes to modFiles.
            var patch = System.Xml.Linq.XDocument.Parse(specification["profileSettingsXml"]!.GetValue<string>()).Root!;
            var item = Assert.Single(patch.Descendants().Where(e => e.Name.LocalName == "ModItem"));
            Assert.Equal("123456", item.Elements().Single(e => e.Name.LocalName == "PublishedFileId").Value);
            Assert.Equal(["123456.sbm"], Directory.GetDirectories(payload).Select(Path.GetFileName));
            Assert.Equal("123456.sbm", item.Elements().Single(e => e.Name.LocalName == "Name").Value);
            Assert.Equal(["123456.sbm/Data/Scripts/Mod.cs"], specification["modFiles"]!.AsObject().Select(p => p.Key));
            Assert.Equal(42, specification["modVersions"]!["123456"]!.GetValue<long>());

            ClusterSetupService.CleanupTransferFiles(work);
            Assert.False(Directory.Exists(Path.Combine(work, "mod-stage")));
        }
        finally { Directory.Delete(work, true); }
    }

    [Fact]
    public void BundledAgentGetsTraceableLoaderCompatibleMetadataWithoutChangingItsBytes()
    {
        string config = Path.Combine(Path.GetTempPath(), "cluster-agent-bundle-" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(config, "Local"));
        Directory.CreateDirectory(Path.Combine(config, "Profiles"));
        try
        {
            foreach (string name in new[] { "Quasar.Agent.dll", "Magnetar.Protocol.dll", "0Harmony.dll" })
                File.WriteAllText(Path.Combine(config, "Local", name), name);
            File.WriteAllText(Path.Combine(config, "Profiles/Current.xml"), "<Profile><Local><string>Quasar.Agent.dll</string></Local></Profile>");
            // Existing clusters retain their old source pin. Profile preparation must replace it.
            string oldBundle = Path.Combine(config, "Local/Quasar.Agent");
            Directory.CreateDirectory(oldBundle);
            File.WriteAllText(Path.Combine(oldBundle, "Quasar.Agent.xml"), "<PluginData><Commit>" + new string('0', 40) + "</Commit></PluginData>");
            ClusterSetupService.PrepareAgentMetadata(config);
            string bundle = Path.Combine(config, "Local/Quasar.Agent");
            Assert.Equal("Quasar.Agent.dll", File.ReadAllText(Path.Combine(bundle, "Quasar.Agent.dll")));
            Assert.False(File.Exists(Path.Combine(config, "Local/Quasar.Agent.dll")));
            var metadata = System.Xml.Linq.XDocument.Load(Path.Combine(bundle, "Quasar.Agent.xml")).Root!;
            Assert.Equal("quasar-agent", metadata.Element("Id")!.Value);
            Assert.Matches("^[a-fA-F0-9]{40}$", metadata.Element("Commit")!.Value);
            Assert.NotEqual(new string('0', 40), metadata.Element("Commit")!.Value);
            Assert.Contains("quasar-agent", File.ReadAllText(Path.Combine(config, "Profiles/Current.xml")));
        }
        finally { Directory.Delete(config, true); }
    }

    [Fact]
    public void GuidedPreparationLetsPinnedHubTransportWinOverDevCheckout()
    {
        string config = Path.Combine(Path.GetTempPath(), "cluster-transport-source-" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(config, "Sources"));
        try
        {
            string path = Path.Combine(config, "Sources/sources.xml");
            File.WriteAllText(path, """
                <SourcesConfig>
                  <RemoteHubSources><RemoteHub><Repo>CometWorks/magnetar-hub</Repo></RemoteHub></RemoteHubSources>
                  <LocalPluginSources>
                    <LocalPlugin><Name>direct-transport</Name><Folder>/dev/direct-transport</Folder></LocalPlugin>
                    <LocalPlugin><Name>other-plugin</Name><Folder>/dev/other-plugin</Folder></LocalPlugin>
                  </LocalPluginSources>
                </SourcesConfig>
                """);
            ClusterSetupService.RemoveDirectTransportDevSource(config);
            var sources = System.Xml.Linq.XDocument.Load(path).Root!;
            Assert.Equal("CometWorks/magnetar-hub", sources.Element("RemoteHubSources")!.Element("RemoteHub")!.Element("Repo")!.Value);
            Assert.Equal("other-plugin", sources.Element("LocalPluginSources")!.Elements("LocalPlugin").Single().Element("Name")!.Value);
        }
        finally { Directory.Delete(config, true); }
    }

    [Fact]
    public void ManagedExportBuildsDevFoldersInRelease()
    {
        string config = Path.Combine(Path.GetTempPath(), "cluster-release-build-" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(config, "Profiles"));
        try
        {
            string path = Path.Combine(config, "Profiles/Current.xml");
            File.WriteAllText(path, """
                <Profile>
                  <GitHub><GitHubPluginConfig><Id>hub-plugin</Id><SelectedVersion /></GitHubPluginConfig></GitHub>
                  <DevFolder>
                    <LocalFolderConfig><Id>debug-plugin</Id><DebugBuild>true</DebugBuild></LocalFolderConfig>
                    <LocalFolderConfig><Id>release-plugin</Id><DebugBuild>false</DebugBuild></LocalFolderConfig>
                  </DevFolder>
                </Profile>
                """);
            ClusterSetupService.UseReleaseBuilds(config);
            var profile = System.Xml.Linq.XDocument.Load(path).Root!;
            Assert.Equal(["false", "false"], profile.Element("DevFolder")!.Elements("LocalFolderConfig").Select(c => c.Element("DebugBuild")!.Value));
            Assert.Equal("hub-plugin", profile.Element("GitHub")!.Element("GitHubPluginConfig")!.Element("Id")!.Value);
        }
        finally { Directory.Delete(config, true); }
    }

    [Fact]
    public void ManagedExportBuildsCoreDevOverridesInRelease()
    {
        string config = Path.Combine(Path.GetTempPath(), "cluster-core-override-" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(config, "Profiles"));
        Directory.CreateDirectory(Path.Combine(config, "Sources"));
        try
        {
            string path = Path.Combine(config, "Profiles/Current.xml");
            File.WriteAllText(path, """
                <Profile>
                  <DevFolder><LocalFolderConfig><Id>selected-plugin</Id><DebugBuild>true</DebugBuild></LocalFolderConfig></DevFolder>
                </Profile>
                """);
            File.WriteAllText(Path.Combine(config, "Sources/sources.xml"), """
                <SourcesConfig><LocalPluginSources>
                  <LocalPlugin><Name>selected-plugin</Name><Folder>/src/selected</Folder><File>Plugin.xml</File><Enabled>true</Enabled></LocalPlugin>
                  <LocalPlugin><Name>unselected-plugin</Name><Folder>/src/unselected</Folder><File>Plugin.xml</File><Enabled>true</Enabled></LocalPlugin>
                  <LocalPlugin><Name>linux-compat</Name><Folder>/src/linux-compat</Folder><File>ServerPlugin/ServerPlugin.xml</File><Enabled>true</Enabled></LocalPlugin>
                </LocalPluginSources></SourcesConfig>
                """);
            ClusterSetupService.UseReleaseBuilds(config);
            ClusterSetupService.UseReleaseBuilds(config);
            var entries = System.Xml.Linq.XDocument.Load(path).Root!.Element("DevFolder")!.Elements("LocalFolderConfig").ToArray();
            Assert.Equal(["selected-plugin", "linux-compat"], entries.Select(e => e.Element("Id")!.Value));
            Assert.All(entries, e => Assert.Equal("false", e.Element("DebugBuild")!.Value));
        }
        finally { Directory.Delete(config, true); }
    }

    [Fact]
    public void DevBuildsNameThePluginsTheExportBuiltFromDevFolders()
    {
        string root = Path.Combine(Path.GetTempPath(), "cluster-dev-builds-" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(root, "exported"));
        Directory.CreateDirectory(Path.Combine(root, "config/Sources"));
        try
        {
            File.WriteAllText(Path.Combine(root, "config/Sources/sources.xml"), """
                <SourcesConfig><LocalPluginSources>
                  <LocalPlugin><Name>linux-compat</Name><Folder>/src/linux-compat</Folder><File>ServerPlugin/ServerPlugin.xml</File><Enabled>true</Enabled></LocalPlugin>
                </LocalPluginSources></SourcesConfig>
                """);
            File.WriteAllText(Path.Combine(root, "exported/preparation.json"), $$"""
                {"plugins": [
                  {"id": "bugfixes", "repository": "CometWorks/bugfixes", "commit": "{{new string('a', 40)}}", "source": "github"},
                  {"id": "linux-compat", "repository": "CometWorks/linux-compat", "commit": "3e187ee9{{new string('b', 32)}}", "source": "dev-folder"}
                ]}
                """);
            Assert.Equal(["linux-compat 3e187ee9bbbb from /src/linux-compat"],
                ClusterSetupService.DevBuilds(Path.Combine(root, "exported"), Path.Combine(root, "config")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void NodeBinaryVersionIsTheGameAssemblyVersionTheRegistryCompares()
    {
        string installation = Path.Combine(Path.GetTempPath(), "cluster-setup-binary-" + Guid.NewGuid());
        string ds = Path.Combine(installation, "Dependencies/payload/DedicatedServer/DedicatedServer64");
        try
        {
            Directory.CreateDirectory(ds);
            Assert.Throws<InvalidDataException>(() => ClusterSetupService.NodeBinaryVersion(installation));
            // Any managed assembly stands in for Sandbox.Game.dll: the value is its assembly version, four-part.
            File.Copy(typeof(ClusterSetupService).Assembly.Location, Path.Combine(ds, "Sandbox.Game.dll"));
            Assert.Equal(typeof(ClusterSetupService).Assembly.GetName().Version!.ToString(), ClusterSetupService.NodeBinaryVersion(installation));
        }
        finally { Directory.Delete(installation, true); }
    }

    [Fact]
    public void LocalCompanionWithoutProvenanceIsLeftOutOfTheClusterProfileAndNamed()
    {
        string config = Path.Combine(Path.GetTempPath(), "cluster-setup-local-" + Guid.NewGuid());
        try
        {
            Directory.CreateDirectory(Path.Combine(config, "Local")); Directory.CreateDirectory(Path.Combine(config, "Profiles"));
            foreach (string name in new[] { "CometWorks.EntityViewer.Magnetar.dll", "Pinned.dll", "Pinned.xml", "AlsoPinned.dll", "AlsoPinned.dll.xml" })
                File.WriteAllText(Path.Combine(config, "Local", name), name);
            string profile = Path.Combine(config, "Profiles/Current.xml");
            File.WriteAllText(profile, "<Profile><Local><string>quasar-agent</string><string>CometWorks.EntityViewer.Magnetar.dll</string>"
                + "<string>Pinned.dll</string><string>AlsoPinned.dll</string></Local></Profile>");

            var excluded = ClusterSetupService.ExcludeLocalPluginsWithoutProvenance(config);

            Assert.Equal(["CometWorks.EntityViewer.Magnetar"], excluded);
            var kept = System.Xml.Linq.XDocument.Load(profile).Root!.Element("Local")!.Elements().Select(e => e.Value).ToArray();
            Assert.Equal(["quasar-agent", "Pinned.dll", "AlsoPinned.dll"], kept);
            Assert.Empty(ClusterSetupService.ExcludeLocalPluginsWithoutProvenance(config));
        }
        finally { Directory.Delete(config, true); }
    }

    [Fact]
    public void HubInstalledCompanionIsPinnedToItsPluginRepositoryAtTheInstalledCommit()
    {
        string root = Path.Combine(Path.GetTempPath(), "cluster-setup-companion-" + Guid.NewGuid());
        string config = Path.Combine(root, "Magnetar"), local = Path.Combine(config, "Local"), profile = Path.Combine(config, "Profiles/Current.xml");
        try
        {
            var hub = Companion(root, "viewer", "cometworks.entityviewer", "CometWorks.EntityViewer.Magnetar.dll",
                new() { RepoId = "CometWorks/viewer", Commit = "7864F2C3AAF14B5F8DCBBE533FA0A6A2D8E17C8B" });
            var dev = Companion(root, "dev", "dev.companion", "Dev.Companion.dll", null);
            var shortCommit = Companion(root, "short", "short.companion", "Short.Companion.dll", new() { RepoId = "CometWorks/short", Commit = "7864f2c" });
            Directory.CreateDirectory(local); Directory.CreateDirectory(Path.GetDirectoryName(profile)!);
            foreach (string name in new[] { "CometWorks.EntityViewer.Magnetar.dll", "Viewer.Shared.dll", "Dev.Companion.dll", "Short.Companion.dll",
                         "Magnetar.Protocol.dll", "0Harmony.dll" })
                File.WriteAllText(Path.Combine(local, name), name);
            File.WriteAllText(profile, "<Profile><Local><string>quasar-agent</string><string>CometWorks.EntityViewer.Magnetar.dll</string>"
                + "<string>Dev.Companion.dll</string><string>Short.Companion.dll</string></Local></Profile>");

            ClusterSetupService.PrepareCompanionMetadata(config, [hub, dev, shortCommit]);

            string bundle = Path.Combine(local, "CometWorks.EntityViewer.Magnetar");
            Assert.Equal(["0Harmony.dll", "CometWorks.EntityViewer.Magnetar.dll", "CometWorks.EntityViewer.Magnetar.xml", "Magnetar.Protocol.dll", "Viewer.Shared.dll"],
                Directory.GetFiles(bundle).Select(Path.GetFileName).Order(StringComparer.Ordinal));
            Assert.False(File.Exists(Path.Combine(local, "CometWorks.EntityViewer.Magnetar.dll")));
            var metadata = System.Xml.Linq.XDocument.Load(Path.Combine(bundle, "CometWorks.EntityViewer.Magnetar.xml")).Root!;
            Assert.Equal("GitHubPlugin", metadata.Attribute(System.Xml.Linq.XName.Get("type", "http://www.w3.org/2001/XMLSchema-instance"))!.Value);
            Assert.Equal("cometworks.entityviewer", metadata.Element("Id")!.Value);
            Assert.Equal("CometWorks/viewer", metadata.Element("RepoId")!.Value);
            Assert.Equal("7864f2c3aaf14b5f8dcbbe533fa0a6a2d8e17c8b", metadata.Element("Commit")!.Value);
            Assert.Equal(["quasar-agent", "cometworks.entityviewer", "Dev.Companion.dll", "Short.Companion.dll"],
                System.Xml.Linq.XDocument.Load(profile).Root!.Element("Local")!.Elements().Select(e => e.Value));
            // Companions that cannot be attributed are still left out and named.
            Assert.Equal(["Dev.Companion", "Short.Companion"], ClusterSetupService.ExcludeLocalPluginsWithoutProvenance(config));
        }
        finally { Directory.Delete(root, true); }
    }

    private static UiPluginCompanion Companion(string root, string plugin, string id, string entry, Quasar.Services.Plugins.QuasarUiPluginInstallMetadata? install)
    {
        string directory = Path.Combine(root, "UiPlugins", plugin), output = Path.Combine(directory, ".quasar/companions", id);
        Directory.CreateDirectory(output);
        // The build output also holds the shared assemblies the runtime preparer never deploys per companion.
        foreach (string name in new[] { entry, "Magnetar.Protocol.dll", "PluginSdk.dll" }.Concat(plugin == "viewer" ? ["Viewer.Shared.dll"] : []))
            File.WriteAllText(Path.Combine(output, name), name);
        if (install is not null)
            File.WriteAllText(Path.Combine(directory, Quasar.Services.Plugins.QuasarUiPluginHubCatalogService.InstallMetadataFileName),
                JsonSerializer.Serialize(install, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return new(id, plugin, plugin, directory, output, Path.Combine(output, entry));
    }

    [Fact]
    public async Task OlderMagnetarIsOnlyInvokedWithHelpAndNeverStartedAsAServer()
    {
        if (!OperatingSystem.IsLinux()) return;
        string folder = Path.Combine(Path.GetTempPath(), "old-magnetar-" + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        try
        {
            string executable = Path.Combine(folder, "launcher");
            await File.WriteAllTextAsync(executable, "#!/bin/sh\nif [ \"$1\" = '-help' ]; then echo 'Older Magnetar help'; exit 0; fi\ntouch '" + Path.Combine(folder, "started") + "'\n");
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await Assert.ThrowsAsync<InvalidOperationException>(() => ClusterSetupService.RequirePreparationCommandAsync(executable, default));
            Assert.False(File.Exists(Path.Combine(folder, "started")));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void MachineInstallationRoutesRequireSecurityAdministration()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<ClusterHostCatalog>(_ => null!);
        builder.Services.AddSingleton<ClusterHostTunnels>(_ => null!);
        builder.Services.AddSingleton<ClusterHostInstaller>(_ => null!);
        var app = builder.Build(); app.MapClusterHostEnrollmentApi();
        var routes = ((IEndpointRouteBuilder)app).DataSources.SelectMany(s => s.Endpoints).OfType<RouteEndpoint>();
        foreach (var endpoint in routes.Where(e => e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains("POST")))
            Assert.Contains(endpoint.Metadata.GetOrderedMetadata<Microsoft.AspNetCore.Authorization.IAuthorizeData>(), p => p.Policy == QuasarPolicyNames.CanManageSecurity);
    }
}
