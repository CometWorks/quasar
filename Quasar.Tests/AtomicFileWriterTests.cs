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

    [WindowsFact]
    public async Task RewriteWaitsForABriefHoldOnTheTarget()
    {
        var path = Path.Combine(_root, "held.json");
        await File.WriteAllTextAsync(path, "old");

        // A reader without delete sharing makes replacing the file fail until it lets go.
        var hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var release = Task.Run(async () =>
        {
            await Task.Delay(300);
            hold.Dispose();
        });

        await AtomicFileWriter.WriteTextAsync(path, "new");
        await release;

        Assert.Equal("new", await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.GetFiles(_root, ".*.tmp"));
    }

    [WindowsFact]
    public async Task RewriteStillFailsAndLeavesNoTempFileWhenTheTargetStaysLocked()
    {
        var path = Path.Combine(_root, "locked.json");
        await File.WriteAllTextAsync(path, "old");

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            var error = await Assert.ThrowsAnyAsync<Exception>(() => AtomicFileWriter.WriteTextAsync(path, "new"));
            Assert.True(error is IOException or UnauthorizedAccessException, error.GetType().Name);
        }

        Assert.Equal("old", await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.GetFiles(_root, ".*.tmp"));
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
