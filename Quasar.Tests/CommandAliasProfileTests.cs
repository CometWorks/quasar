using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bunit;
using Magnetar.Protocol.Runtime;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using PluginSdk.Commands;
using PluginSdk.Config;
using Quasar.Agent;
using Quasar.Components;
using Quasar.Models;
using Quasar.Services;
using Quasar.Services.Plugins;
using Xunit;

namespace Quasar.Tests;

[Collection("Exact server creation")]
public sealed class CommandAliasProfileTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "quasar-aliases-" + Guid.NewGuid().ToString("N"));
    private readonly FieldInfo cache = typeof(MagnetarPaths).GetField("_cachedQuasarDirectory", BindingFlags.NonPublic | BindingFlags.Static)!;
    private readonly object? previous;
    public CommandAliasProfileTests() { previous = cache.GetValue(null); cache.SetValue(null, root); Directory.CreateDirectory(root); }
    public void Dispose() { cache.SetValue(null, previous); Directory.Delete(root, true); }
    private static QuasarConfigProfile Profile() => new() { Name = "Aliases", CommandAliases = [new() { Source = "!grids list", Target = "!ess grids list" }] };

    [Fact]
    public async Task CatalogPersistsClonesAndIsolatesProfilesAndRejectsInvalidUpdates()
    {
        using (var catalog = new QuasarConfigProfileCatalog(NullLogger<QuasarConfigProfileCatalog>.Instance))
        {
            var profile = Profile();
            await catalog.UpsertAsync(profile);
            var clone = catalog.GetProfile(profile.ConfigProfileId)!;
            clone.ConfigProfileId = "copy";
            clone.CommandAliases![0].Target = "!other list";
            await catalog.UpsertAsync(clone);
            Assert.Equal("!ess grids list", catalog.GetProfile(profile.ConfigProfileId)!.CommandAliases![0].Target);
            clone.CommandAliases.Add(new() { Source = "!GRIDS\tLIST", Target = "!other" });
            await Assert.ThrowsAsync<ArgumentException>(() => catalog.UpsertAsync(clone));
            Assert.Single(catalog.GetProfile("copy")!.CommandAliases!);
        }
        using var reloaded = new QuasarConfigProfileCatalog(NullLogger<QuasarConfigProfileCatalog>.Instance);
        Assert.Equal("!other list", reloaded.GetProfile("copy")!.CommandAliases![0].Target);
        Assert.DoesNotContain("commandAliases", JsonSerializer.Serialize(new QuasarConfigProfile(), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    [Theory]
    [InlineData("!help", "!ess")]
    [InlineData("!a", "!A")]
    [InlineData("a", "!ess")]
    [InlineData("!a\n", "!ess")]
    [InlineData("!a", "!ess \"fixed argument\"")]
    public void RejectsInvalidAliasPaths(string source, string target) =>
        Assert.Throws<ArgumentException>(() => QuasarCommandAliases.Validate([new() { Source = source, Target = target }]));

    [Fact]
    public async Task RuntimePreparationReplacesThenClearsProfileAliases()
    {
        var devFolders = new QuasarDevFolderCatalog(NullLogger<QuasarDevFolderCatalog>.Instance);
        using var profiles = new QuasarConfigProfileCatalog(NullLogger<QuasarConfigProfileCatalog>.Instance);
        using var consent = new DataHandlingConsentCatalog(NullLogger<DataHandlingConsentCatalog>.Instance);
        using var credentials = new GitHubUpdateCredentialsCatalog(NullLogger<GitHubUpdateCredentialsCatalog>.Instance, new EphemeralDataProtectionProvider(), _ => null);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Quasar:Plugins:SafeMode"] = "true" }).Build();
        var uiPlugins = QuasarUiPluginCatalog.Create(configuration, new HostingEnvironment { ContentRootPath = root });
        var catalog = new QuasarPluginCatalogService(NullLogger<QuasarPluginCatalogService>.Instance, new NoNetwork(), devFolders);
        var preparer = new DedicatedServerRuntimePreparer(NullLogger<DedicatedServerRuntimePreparer>.Instance,
            new WebServiceOptions(), consent, profiles, catalog, devFolders, uiPlugins, credentials);
        string sdk = Path.Combine(root, "runtime/Libraries/MagnetarInterim/PluginSdk.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(sdk)!);
        File.Copy(typeof(ServerCommands).Assembly.Location, sdk);
        var definition = new DedicatedServerDefinition { UniqueName = "aliases", WorldSaveName = "World", ExecutablePath = Path.Combine(root, "runtime/MagnetarInterim.bin") };
        Directory.CreateDirectory(definition.GetWorldSavePath());
        File.WriteAllText(Path.Combine(definition.GetWorldSavePath(), "Sandbox.sbc"), "<MyObjectBuilder_Checkpoint />");
        var profile = Profile();
        var prepared = await preparer.PrepareAsync(definition, Path.Combine(root, "DS64"), MagnetarLaunchArgumentStyle.Current, profileOverride: profile);
        string file = Path.Combine(prepared.MagnetarAppDataPath, QuasarCommandAliases.FileName);
        Assert.Equal("!ess grids list", ConfigStorage.LoadJson<CommandAliasConfiguration>(File.ReadAllText(file)).Aliases["!grids list"]);
        profile.CommandAliases = null;
        await preparer.PrepareAsync(definition, Path.Combine(root, "DS64"), MagnetarLaunchArgumentStyle.Current, profileOverride: profile);
        Assert.Empty(ConfigStorage.LoadJson<CommandAliasConfiguration>(File.ReadAllText(file)).Aliases);
    }

    [Fact]
    public void MissingRuntimeFailsOnlyForConfiguredAliases()
    {
        QuasarCommandAliases.RequireRuntime(new(), "missing");
        Assert.Contains("Update Magnetar", Assert.Throws<InvalidOperationException>(() => QuasarCommandAliases.RequireRuntime(Profile(), "missing")).Message);
    }

    [Fact]
    public void RuntimeCheckUsesTheSelectedLaunchersSdkAndRejectsUncontractedAssemblies()
    {
        string legacySdk = Path.Combine(root, "Libraries/MagnetarLegacy/PluginSdk.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(legacySdk)!);
        File.Copy(typeof(ServerCommands).Assembly.Location, legacySdk);
        QuasarCommandAliases.RequireRuntime(Profile(), Path.Combine(root, "MagnetarLegacy.exe"));
        Assert.Throws<InvalidOperationException>(() => QuasarCommandAliases.RequireRuntime(Profile(), Path.Combine(root, "MagnetarInterim.bin")));
        string interimSdk = Path.Combine(root, "Libraries/MagnetarInterim/PluginSdk.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(interimSdk)!);
        File.Copy(typeof(QuasarConfigProfile).Assembly.Location, interimSdk);
        Assert.Throws<InvalidOperationException>(() => QuasarCommandAliases.RequireRuntime(Profile(), Path.Combine(root, "MagnetarInterim.bin")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CanonicalClusterValuesOverrideOldAliasesAndClearOnRemoval(bool multiple)
    {
        var config = new JsonObject { ["configType"] = QuasarCommandAliases.ConfigType,
            ["configuration"] = JsonNode.Parse(ConfigStorage.SaveJson(new CommandAliasConfiguration())) };
        var canonical = new JsonObject { ["agent"] = multiple ? new JsonObject { ["configurations"] = new JsonArray(config) } : config };
        QuasarCommandAliases.ApplyToCanonical(canonical, Profile());
        var values = ConfigStorage.LoadJson<CommandAliasConfiguration>(config["configuration"]!.ToJsonString());
        Assert.Equal("!ess grids list", values.Aliases["!grids list"]);
        QuasarCommandAliases.ApplyToCanonical(canonical, new());
        Assert.Empty(ConfigStorage.LoadJson<CommandAliasConfiguration>(config["configuration"]!.ToJsonString()).Aliases);
        Assert.Throws<InvalidOperationException>(() => QuasarCommandAliases.ApplyToCanonical(new(), Profile()));
    }

    [Fact]
    public void EditorAddsEditsAndRemovesProfileRows()
    {
        using var context = new BunitContext();
        var profile = new QuasarConfigProfile();
        var rendered = context.Render<CommandAliasesEditor>(p => p.Add(c => c.Profile, profile));
        rendered.Find("button").Click();
        rendered.FindAll("input")[0].Input("!grids");
        rendered.FindAll("input")[1].Input("!ess grids");
        Assert.Equal("!grids", Assert.Single(profile.CommandAliases!).Source);
        Assert.Equal("!ess grids", profile.CommandAliases![0].Target);
        rendered.Find("button").Click();
        Assert.Null(profile.CommandAliases);
    }

    private sealed class NoNetwork : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("This test must not access the network.");
    }
}
