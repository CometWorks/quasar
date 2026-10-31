using Quasar.Services.Backup;
using Xunit;

namespace Quasar.Tests;

public sealed class BackupFileSecurityTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "backup-file-security-" + Guid.NewGuid().ToString("N"));

    public BackupFileSecurityTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task StoredBackupIsOwnerOnly()
    {
        if (OperatingSystem.IsWindows())
            return;

        var path = Path.Combine(_root, "backup.zip.tmp");
        await using (var stream = QuasarBackupService.OpenPrivateWrite(path))
            await stream.WriteAsync("backup"u8.ToArray());
        var completed = Path.Combine(_root, "backup.zip");
        File.Move(path, completed);

        Assert.Equal((UnixFileMode)0x180, File.GetUnixFileMode(completed));
    }
}
