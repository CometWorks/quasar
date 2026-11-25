using System.Text.Json;
using Magnetar.Protocol.Runtime;
using Quasar.Updates;
using Xunit;

namespace Quasar.Tests;

public sealed class QuasarApplicationPackageTests
{
    private const string Version = "1.7.1.0";

    [Fact]
    public async Task PackageWithoutWebUiMarkerRemainsALegacyRollbackTarget()
    {
        var directory = await CreatePackageAsync();
        try
        {
            QuasarApplicationPackage.Validate(directory, Version);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PackageWithWebUiMarkerRequiresTheNativeUiAssets()
    {
        var directory = await CreatePackageAsync(webUi: 1);
        try
        {
            QuasarApplicationPackage.Validate(directory, Version);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DeclaredPackageWithoutANativeUiAssetIsIncomplete()
    {
        var directory = await CreatePackageAsync(webUi: 1, omit: "wwwroot/quasar-ui.js");
        try
        {
            var error = Assert.Throws<InvalidDataException>(
                () => QuasarApplicationPackage.Validate(directory, Version));
            Assert.Contains("Incomplete native UI package: wwwroot/quasar-ui.js", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task UnsupportedWebUiMarkerIsRejected()
    {
        var directory = await CreatePackageAsync(webUi: 2);
        try
        {
            var error = Assert.Throws<InvalidDataException>(
                () => QuasarApplicationPackage.Validate(directory, Version));
            Assert.Contains("Unsupported web UI package", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<string> CreatePackageAsync(int? webUi = null, string? omit = null)
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "quasar-application-package-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        var metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["version"] = Version,
            ["updaterProtocol"] = 1,
        };
        if (webUi is not null)
            metadata["webUi"] = webUi.Value;
        await File.WriteAllTextAsync(
            Path.Combine(directory, "release.json"), JsonSerializer.Serialize(metadata));

        var files = new List<string>
        {
            "release.json",
            QuasarWebReleaseLayout.WorkerExecutableFileName,
        };
        files.AddRange(QuasarWebReleaseLayout.RequiredWebAssetFiles);
        if (webUi == 1)
            files.AddRange(QuasarWebReleaseLayout.NativeUiWebAssetFiles);

        var included = files
            .Where(name => !string.Equals(name, omit, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        foreach (var name in included)
        {
            if (name == "release.json")
                continue;

            var path = Path.Combine(directory, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, "fixture");
        }

        await File.WriteAllLinesAsync(Path.Combine(directory, "launcher-files.txt"), included);
        return directory;
    }
}
