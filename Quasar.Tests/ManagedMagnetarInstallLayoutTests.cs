using Quasar.Services;
using Xunit;

namespace Quasar.Tests;

public sealed class ManagedMagnetarInstallLayoutTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "quasar-tests",
        $"magnetar-layout-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public void FindMagnetarSourceRecognizesRootLayoutArchive()
    {
        var archiveRoot = CreateRootLayout(Path.Combine(_root, "extract", "Magnetar"));

        var source = ManagedDedicatedServerRuntimeResolver.FindMagnetarSource(Path.Combine(_root, "extract"));

        Assert.NotNull(source);
        Assert.Equal(ManagedDedicatedServerRuntimeResolver.MagnetarSourceLayout.Root, source.Layout);
        Assert.Equal(archiveRoot, source.Directory);
        Assert.Equal(archiveRoot, source.PayloadDirectory);
        Assert.Equal(Path.Combine(archiveRoot, "MagnetarInterim.bin"), source.LauncherPath);
    }

    [Fact]
    public void FindMagnetarSourceStillAcceptsLegacyBinLayoutArchive()
    {
        var archiveRoot = CreateBinLayout(Path.Combine(_root, "extract", "Magnetar"));

        var source = ManagedDedicatedServerRuntimeResolver.FindMagnetarSource(Path.Combine(_root, "extract"));

        Assert.NotNull(source);
        Assert.Equal(ManagedDedicatedServerRuntimeResolver.MagnetarSourceLayout.Bin, source.Layout);
        Assert.Equal(archiveRoot, source.Directory);
        Assert.Equal(Path.Combine(archiveRoot, "Bin"), source.PayloadDirectory);
        Assert.Equal(Path.Combine(archiveRoot, "MagnetarInterim"), source.LauncherPath);
    }

    [Fact]
    public void FindMagnetarSourceReturnsNullWithoutPayload()
    {
        var bare = Path.Combine(_root, "extract", "Magnetar");
        Directory.CreateDirectory(bare);
        File.WriteAllText(Path.Combine(bare, "MagnetarInterim.bin"), string.Empty);

        Assert.Null(ManagedDedicatedServerRuntimeResolver.FindMagnetarSource(Path.Combine(_root, "extract")));
        Assert.Null(ManagedDedicatedServerRuntimeResolver.FindMagnetarSource(Path.Combine(_root, "missing")));
    }

    [Fact]
    public void InstalledLauncherPrefersRootApphostAndFallsBackToBin()
    {
        var rootInstall = CreateRootLayout(Path.Combine(_root, "install-root"));
        var binInstall = CreateBinLayout(Path.Combine(_root, "install-bin"));
        // An old install never had the wrapper at the root, only Bin/ was copied.
        File.Delete(Path.Combine(binInstall, "MagnetarInterim"));

        Assert.Equal(
            Path.Combine(rootInstall, "MagnetarInterim.bin"),
            ManagedDedicatedServerRuntimeResolver.FindInstalledLinuxMagnetarLauncherPath(rootInstall));
        Assert.Equal(
            Path.Combine(binInstall, "Bin", "MagnetarInterim"),
            ManagedDedicatedServerRuntimeResolver.FindInstalledLinuxMagnetarLauncherPath(binInstall));
        Assert.Null(ManagedDedicatedServerRuntimeResolver.FindInstalledLinuxMagnetarLauncherPath(Path.Combine(_root, "missing")));
        Assert.Null(ManagedDedicatedServerRuntimeResolver.FindInstalledLinuxMagnetarLauncherPath(string.Empty));
    }

    [Fact]
    public void PluginSdkProbePrefersLibrariesAndFallsBackToBin()
    {
        var rootInstall = CreateRootLayout(Path.Combine(_root, "install-root"));
        var binInstall = CreateBinLayout(Path.Combine(_root, "install-bin"));

        Assert.Equal(
            Path.Combine(rootInstall, "Libraries", "MagnetarInterim", "PluginSdk.dll"),
            ManagedDedicatedServerRuntimeResolver.FindLinuxPluginSdkPath(rootInstall));
        Assert.Equal(
            Path.Combine(binInstall, "Bin", "PluginSdk.dll"),
            ManagedDedicatedServerRuntimeResolver.FindLinuxPluginSdkPath(binInstall));
        Assert.Null(ManagedDedicatedServerRuntimeResolver.FindLinuxPluginSdkPath(Path.Combine(_root, "missing")));
    }

    // pulsar-based bundle: apphost + framework files at the root, payload under Libraries/.
    private static string CreateRootLayout(string root)
    {
        Directory.CreateDirectory(root);
        foreach (var name in new[]
                 {
                     "MagnetarInterim.bin",
                     "MagnetarInterim.dll",
                     "MagnetarInterim.deps.json",
                     "MagnetarInterim.runtimeconfig.json",
                     "MagnetarConfig.bin",
                     "LICENSE",
                     "README.md",
                 })
        {
            File.WriteAllText(Path.Combine(root, name), string.Empty);
        }

        Directory.CreateDirectory(Path.Combine(root, "Libraries", "MagnetarInterim"));
        File.WriteAllText(Path.Combine(root, "Libraries", "MagnetarInterim", "PluginSdk.dll"), string.Empty);
        Directory.CreateDirectory(Path.Combine(root, "Libraries", "Compiler"));
        File.WriteAllText(Path.Combine(root, "Libraries", "Compiler", "Compiler"), string.Empty);
        Directory.CreateDirectory(Path.Combine(root, "Libraries", "MagnetarConfig"));
        return root;
    }

    // main-branch bundle: wrapper script at the root, apphost and flat payload under Bin/.
    private static string CreateBinLayout(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "Bin"));
        File.WriteAllText(Path.Combine(root, "MagnetarInterim"), "#!/bin/sh\n");
        File.WriteAllText(Path.Combine(root, "Bin", "MagnetarInterim"), string.Empty);
        File.WriteAllText(Path.Combine(root, "Bin", "MagnetarInterim.dll"), string.Empty);
        File.WriteAllText(Path.Combine(root, "Bin", "PluginSdk.dll"), string.Empty);
        return root;
    }
}
