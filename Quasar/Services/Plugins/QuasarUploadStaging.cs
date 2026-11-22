using Magnetar.Protocol.Runtime;
using Quasar.Plugin.Abstractions.Operations;

namespace Quasar.Services.Plugins;

public static class QuasarUploadStaging
{
    public static async Task<FileStream> StageAsync(Stream source, long maximumBytes, CancellationToken token, Action? progress = null)
    {
        if (maximumBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        var staged = CreateUpload();
        try
        {
            await CopyUploadAsync(source, staged, maximumBytes, token, progress);
            staged.Position = 0;
            return staged;
        }
        catch { await staged.DisposeAsync(); throw; }
    }

    private static FileStream CreateUpload()
    {
        var directory = Path.Combine(MagnetarPaths.GetQuasarDirectory(), "SdkUploads");
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
        else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        foreach (var file in Directory.EnumerateFiles(directory, "*.upload"))
            if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddHours(-1))
                try { File.Delete(file); } catch (IOException) { }
        var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".upload");
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite,
            Share = FileShare.None, Options = FileOptions.Asynchronous | FileOptions.DeleteOnClose };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(path, options);
    }

    private static async Task CopyUploadAsync(Stream source, Stream destination, long maximum, CancellationToken token, Action? progress)
    {
        var buffer = new byte[QuasarSdkLimits.TransferBufferBytes];
        long total = 0;
        int count;
        while ((count = await source.ReadAsync(buffer, token)) > 0)
        {
            total += count;
            if (total > maximum) throw new QuasarOperationException(413, "The upload exceeds the registered operation limit.");
            await destination.WriteAsync(buffer.AsMemory(0, count), token);
            progress?.Invoke();
        }
        await destination.FlushAsync(token);
    }

}
