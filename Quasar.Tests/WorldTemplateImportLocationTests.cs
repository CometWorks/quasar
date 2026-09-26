using Quasar.Services;
using Xunit;

namespace Quasar.Tests;

public sealed class WorldTemplateImportLocationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "world-template-sources-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    [Fact]
    public void SameWorldInTwoDedicatedServerInstallsIsListedOnceFromTheFirstInstallRoot()
    {
        string configured = Install("steam", "Empty World", "Green Station");
        string managed = Install("managed", "Empty World", "Only Managed");
        var service = new WorldTemplateImportLocationService(new ManagedRuntimeOptions
        {
            DedicatedServerInstallDirectory = managed,
            DedicatedServer64OverridePath = Path.Combine(configured, "DedicatedServer64"),
        }, null!);

        // The machine's own managed install is a third root; only the fixture roots are asserted.
        var templates = service.GetInstalledWorldTemplates().Where(t => t.SourcePath.StartsWith(root, StringComparison.Ordinal)).ToArray();

        Assert.Equal(["Empty World", "Green Station", "Only Managed"], templates.Select(t => t.DisplayName).Order());
        Assert.StartsWith(managed, Assert.Single(templates, t => t.DisplayName == "Empty World").SourcePath);
    }

    private string Install(string name, params string[] worlds)
    {
        string install = Path.Combine(root, name);
        Directory.CreateDirectory(Path.Combine(install, "DedicatedServer64"));
        foreach (string world in worlds)
        {
            string directory = Path.Combine(install, "Content", "CustomWorlds", world);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "Sandbox.sbc"), $"<MyObjectBuilder_Checkpoint><SessionName>{world}</SessionName></MyObjectBuilder_Checkpoint>");
        }
        return install;
    }
}
