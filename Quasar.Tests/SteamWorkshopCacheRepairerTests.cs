using Quasar.Services;
using Xunit;

namespace Quasar.Tests;

public sealed class SteamWorkshopCacheRepairerTests
{
    [Fact]
    public void HealthyCacheIsLeftUntouched()
    {
        using var test = new WorkshopCacheTestDirectory();
        test.WriteManifest(758597413);
        test.WriteModMetadata(758597413);

        var result = SteamWorkshopCacheRepairer.RepairIfNeeded(test.Root);

        Assert.False(result.Repaired);
        Assert.True(File.Exists(test.ManifestPath));
        Assert.False(Directory.Exists(Path.Combine(test.Root, "WorkshopCacheQuarantine")));
    }

    [Fact]
    public void MissingInstalledContentQuarantinesManifestAndDownloads()
    {
        using var test = new WorkshopCacheTestDirectory();
        test.WriteManifest(758597413, 3729726343);
        test.WriteModMetadata(758597413);
        var downloadStatePath = Path.Combine(test.Root, "downloads", "state.patch");
        Directory.CreateDirectory(Path.GetDirectoryName(downloadStatePath)!);
        File.WriteAllText(downloadStatePath, "partial");

        var result = SteamWorkshopCacheRepairer.RepairIfNeeded(test.Root);

        Assert.True(result.Repaired);
        Assert.Equal([3729726343UL], result.MissingItemIds);
        Assert.False(File.Exists(test.ManifestPath));
        Assert.True(File.Exists(Path.Combine(result.QuarantinePath, Path.GetFileName(test.ManifestPath))));
        Assert.True(File.Exists(Path.Combine(result.QuarantinePath, "downloads", "state.patch")));
        Assert.True(File.Exists(test.MetadataPath(758597413)));
    }

    [Fact]
    public void MalformedManifestIsQuarantined()
    {
        using var test = new WorkshopCacheTestDirectory();
        File.WriteAllText(test.ManifestPath, "\"AppWorkshop\"\n{\n");

        var result = SteamWorkshopCacheRepairer.RepairIfNeeded(test.Root);

        Assert.True(result.Repaired);
        Assert.Empty(result.MissingItemIds);
        Assert.Contains("invalid manifest", result.Issue);
        Assert.True(File.Exists(Path.Combine(result.QuarantinePath, Path.GetFileName(test.ManifestPath))));
    }

    private sealed class WorkshopCacheTestDirectory : IDisposable
    {
        public WorkshopCacheTestDirectory()
        {
            Root = Path.Combine(Path.GetTempPath(), "quasar-workshop-cache-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string ManifestPath => Path.Combine(Root, "appworkshop_244850.acf");

        public string MetadataPath(ulong itemId) =>
            Path.Combine(Root, "content", "244850", itemId.ToString(), "metadata.mod");

        public void WriteManifest(params ulong[] itemIds)
        {
            var items = string.Join(
                Environment.NewLine,
                itemIds.Select(itemId => $"\t\t\"{itemId}\"\n\t\t{{\n\t\t\t\"size\" \"1\"\n\t\t}}"));
            File.WriteAllText(
                ManifestPath,
                $"\"AppWorkshop\"\n{{\n\t\"appid\" \"244850\"\n\t\"WorkshopItemsInstalled\"\n\t{{\n{items}\n\t}}\n}}\n");
        }

        public void WriteModMetadata(ulong itemId)
        {
            var path = MetadataPath(itemId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "metadata");
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
