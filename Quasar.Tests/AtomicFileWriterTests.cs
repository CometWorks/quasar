using Quasar.Services;
using Xunit;

namespace Quasar.Tests;

public sealed class AtomicFileWriterTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "atomic-file-writer-" + Guid.NewGuid().ToString("N"));

    public AtomicFileWriterTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task RewritePreservesExistingUnixMode()
    {
        if (OperatingSystem.IsWindows())
            return;

        var path = Path.Combine(_root, "settings.json");
        await File.WriteAllTextAsync(path, "old");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        await AtomicFileWriter.WriteTextAsync(path, "new");

        Assert.Equal("new", await File.ReadAllTextAsync(path));
        Assert.Equal((UnixFileMode)0x180, File.GetUnixFileMode(path));
    }

    [Fact]
    public async Task PrivateWriteCreatesOwnerOnlyFile()
    {
        if (OperatingSystem.IsWindows())
            return;

        var path = Path.Combine(_root, "secret.json");

        await AtomicFileWriter.WritePrivateTextAsync(path, "secret");

        Assert.Equal((UnixFileMode)0x180, File.GetUnixFileMode(path));
    }
}
