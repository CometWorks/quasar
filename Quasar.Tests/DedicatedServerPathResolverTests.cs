using Quasar.Models;
using Xunit;

namespace Quasar.Tests;

public sealed class DedicatedServerPathResolverTests
{
    [Fact]
    public void EmptyPathsFollowCurrentQuasarRoot()
    {
        var definition = CreateDefinition();

        var first = DedicatedServerPathResolver.Resolve(definition, Root("first"));
        var second = DedicatedServerPathResolver.Resolve(definition, Root("second"));

        Assert.Equal(Path.Combine(Root("first"), "Magnetars", "test", "DedicatedServer"), first.DedicatedServerAppDataPath);
        Assert.Equal(Path.Combine(Root("second"), "Magnetars", "test", "DedicatedServer"), second.DedicatedServerAppDataPath);
        Assert.Equal(Path.Combine(second.DedicatedServerAppDataPath, "Saves", "World"), second.WorldSavePath);
    }

    [Fact]
    public void RelativeOverridesFollowCurrentQuasarRoot()
    {
        var definition = CreateDefinition();
        definition.DedicatedServerAppDataPath = "Data/Server";
        definition.MagnetarAppDataPath = "Data/Magnetar";
        definition.WorldPath = "Worlds";
        definition.ConfigFilePath = "Config/server.cfg";

        var paths = DedicatedServerPathResolver.Resolve(definition, Root("moved"));

        Assert.Equal(Path.Combine(Root("moved"), "Data", "Server"), paths.DedicatedServerAppDataPath);
        Assert.Equal(Path.Combine(Root("moved"), "Data", "Magnetar"), paths.MagnetarAppDataPath);
        Assert.Equal(Path.Combine(Root("moved"), "Worlds"), paths.SavesPath);
        Assert.Equal(Path.Combine(Root("moved"), "Config", "server.cfg"), paths.ConfigFilePath);
    }

    [Fact]
    public void BlankWorldPathFollowsCustomDedicatedServerPath()
    {
        var definition = CreateDefinition();
        definition.DedicatedServerAppDataPath = "Data/Server";

        var paths = DedicatedServerPathResolver.Resolve(definition, Root("root"));

        Assert.Equal(Path.Combine(Root("root"), "Data", "Server", "Saves"), paths.SavesPath);
    }

    [Fact]
    public void ManagedAbsoluteDefaultsCanonicalizeToBlank()
    {
        var root = Root("old");
        var serverRoot = Path.Combine(root, "Magnetars", "test");
        var definition = CreateDefinition();
        definition.DedicatedServerAppDataPath = Path.Combine(serverRoot, "DedicatedServer");
        definition.MagnetarAppDataPath = Path.Combine(serverRoot, "Magnetar");
        definition.WorldPath = Path.Combine(serverRoot, "DedicatedServer", "Saves");
        definition.ConfigFilePath = Path.Combine(serverRoot, "DedicatedServer", "SpaceEngineers-Dedicated.cfg");

        DedicatedServerPathResolver.CanonicalizeForStorage(definition, root);

        Assert.Empty(definition.DedicatedServerAppDataPath);
        Assert.Empty(definition.MagnetarAppDataPath);
        Assert.Empty(definition.WorldPath);
        Assert.Empty(definition.ConfigFilePath);
    }

    [Fact]
    public void InRootOverridesCanonicalizeToPortableRelativePaths()
    {
        var root = Root("root");
        var definition = CreateDefinition();
        definition.WorldPath = Path.Combine(root, "Shared", "Saves");

        DedicatedServerPathResolver.CanonicalizeForStorage(definition, root);

        Assert.Equal("Shared/Saves", definition.WorldPath);
    }

    [Fact]
    public void ExternalAbsoluteOverridesRemainAbsolute()
    {
        var root = Root("root");
        var external = Root("external");
        var definition = CreateDefinition();
        definition.WorldPath = external;

        DedicatedServerPathResolver.CanonicalizeForStorage(definition, root);

        Assert.Equal(external, definition.WorldPath);
        Assert.Equal(external, DedicatedServerPathResolver.Resolve(definition, root).SavesPath);
    }

    [Fact]
    public void MaterializedDependentDefaultsCollapseBehindExternalDedicatedServerPath()
    {
        var root = Root("root");
        var externalDedicatedServer = Path.Combine(Root("external"), "DedicatedServer");
        var definition = CreateDefinition();
        definition.DedicatedServerAppDataPath = externalDedicatedServer;
        definition.WorldPath = Path.Combine(externalDedicatedServer, "Saves");
        definition.ConfigFilePath = Path.Combine(externalDedicatedServer, "SpaceEngineers-Dedicated.cfg");

        DedicatedServerPathResolver.CanonicalizeForStorage(definition, root);

        Assert.Equal(externalDedicatedServer, definition.DedicatedServerAppDataPath);
        Assert.Empty(definition.WorldPath);
        Assert.Empty(definition.ConfigFilePath);

        definition.DedicatedServerAppDataPath = string.Empty;
        var moved = DedicatedServerPathResolver.Resolve(definition, root);
        Assert.Equal(Path.Combine(root, "Magnetars", "test", "DedicatedServer", "Saves"), moved.SavesPath);
    }

    private static DedicatedServerDefinition CreateDefinition() => new()
    {
        UniqueName = "test",
        WorldSaveName = "World",
    };

    private static string Root(string name) =>
        Path.GetFullPath(Path.Combine(Path.GetTempPath(), "quasar-path-tests", name));
}
