using Quasar.Services;
using Xunit;

namespace Quasar.Tests;

public sealed class SteamWorkshopCacheRepairerTests
{
    [Fact]
    public async Task HealthyCacheWithoutMetadataFileIsLeftUntouched()
    {
        using var test = new WorkshopCacheTestDirectory();
        test.WriteManifest(758597413);
        test.WriteContent(758597413, "Data/Scripts/local-copy.cs");

        var result = await SteamWorkshopCacheRepairer.RepairIfNeededAsync(test.Root);

        Assert.False(result.Repaired);
        Assert.True(File.Exists(test.ManifestPath));
        Assert.False(Directory.Exists(Path.Combine(test.Root, "WorkshopCacheQuarantine")));
    }

    [Fact]
    public async Task MissingItemRemovesOnlyItsInstalledState()
    {
        const ulong healthyItemId = 3082595868;
        const ulong missingItemId = 3154371364;
        using var test = new WorkshopCacheTestDirectory();
        test.WriteManifest(healthyItemId, missingItemId);
        test.WriteContent(healthyItemId, "Data/Scripts/local-copy.cs");
        var downloadStatePath = Path.Combine(test.Root, "downloads", "state.patch");
        Directory.CreateDirectory(Path.GetDirectoryName(downloadStatePath)!);
        File.WriteAllText(downloadStatePath, "partial");

        var result = await SteamWorkshopCacheRepairer.RepairIfNeededAsync(test.Root);

        Assert.True(result.Repaired);
        Assert.False(result.ManifestQuarantined);
        Assert.Equal([missingItemId], result.MissingItemIds);
        Assert.True(File.Exists(test.ManifestPath));
        Assert.Equal(2, CountOccurrences(File.ReadAllText(test.ManifestPath), $"\"{healthyItemId}\""));
        Assert.Equal(1, CountOccurrences(File.ReadAllText(test.ManifestPath), $"\"{missingItemId}\""));
        Assert.True(File.Exists(test.ContentPath(healthyItemId, "Data/Scripts/local-copy.cs")));
        Assert.True(File.Exists(downloadStatePath));
        Assert.False(Directory.Exists(Path.Combine(test.Root, "WorkshopCacheQuarantine")));
    }

    [Fact]
    public async Task MalformedManifestIsQuarantined()
    {
        using var test = new WorkshopCacheTestDirectory();
        File.WriteAllText(test.ManifestPath, "\"AppWorkshop\"\n{\n");

        var result = await SteamWorkshopCacheRepairer.RepairIfNeededAsync(test.Root);

        Assert.True(result.Repaired);
        Assert.True(result.ManifestQuarantined);
        Assert.Empty(result.MissingItemIds);
        Assert.Contains("invalid manifest", result.Issue);
        Assert.True(File.Exists(Path.Combine(result.QuarantinePath, Path.GetFileName(test.ManifestPath))));
    }

    private static int CountOccurrences(string value, string search)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(search, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += search.Length;
        }

        return count;
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

        public string ContentPath(ulong itemId, string relativePath) =>
            Path.Combine(Root, "content", "244850", itemId.ToString(), relativePath);

        public void WriteManifest(params ulong[] itemIds)
        {
            var installedItems = string.Join(
                Environment.NewLine,
                itemIds.Select(itemId => $"\t\t\"{itemId}\"\n\t\t{{\n\t\t\t\"size\" \"1\"\n\t\t}}"));
            var itemDetails = string.Join(
                Environment.NewLine,
                itemIds.Reverse().Select(itemId => $"\t\t\"{itemId}\"\n\t\t{{\n\t\t\t\"manifest\" \"1\"\n\t\t}}"));
            File.WriteAllText(
                ManifestPath,
                $"\"AppWorkshop\"\n{{\n\t\"appid\" \"244850\"\n\t\"WorkshopItemsInstalled\"\n\t{{\n{installedItems}\n\t}}\n\t\"WorkshopItemDetails\"\n\t{{\n{itemDetails}\n\t}}\n}}\n");
        }

        public void WriteContent(ulong itemId, string relativePath)
        {
            var path = ContentPath(itemId, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "local content");
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
