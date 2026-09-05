using Quasar.Services;
using Xunit;

namespace Quasar.Tests;

public sealed class SteamCmdStartInfoTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "quasar-tests",
        $"steamcmd-home-{Guid.NewGuid():N}");

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
    public void LinuxStartInfoRunsSteamCmdWithIsolatedHome()
    {
        if (OperatingSystem.IsWindows())
            return;

        var steamCmdDirectory = Path.Combine(_root, "SteamCMD");
        Directory.CreateDirectory(steamCmdDirectory);
        var steamCmdPath = Path.Combine(steamCmdDirectory, "steamcmd.sh");
        File.WriteAllText(steamCmdPath, "#!/bin/sh\n");
        var homeDirectory = Path.Combine(_root, "SteamCmdHome");
        var installDirectory = Path.Combine(_root, "SpaceEngineersDedicatedServer");

        var startInfo = ManagedDedicatedServerRuntimeResolver.CreateSteamCmdStartInfo(
            steamCmdPath,
            ManagedDedicatedServerRuntimeResolver.BuildDedicatedServerUpdateArguments(installDirectory, validate: false),
            homeDirectory);

        // The real user's HOME (and with it ~/.steam) must never reach SteamCMD.
        Assert.Equal(homeDirectory, startInfo.Environment["HOME"]);
        Assert.NotEqual(Environment.GetEnvironmentVariable("HOME"), startInfo.Environment["HOME"]);
        Assert.Equal(Path.Combine(homeDirectory, ".local", "share"), startInfo.Environment["XDG_DATA_HOME"]);
        Assert.Equal(Path.Combine(homeDirectory, ".config"), startInfo.Environment["XDG_CONFIG_HOME"]);
        Assert.Equal(Path.Combine(homeDirectory, ".cache"), startInfo.Environment["XDG_CACHE_HOME"]);
        Assert.True(Directory.Exists(homeDirectory), "isolated home must exist before SteamCMD starts");

        // The DS install location is still forced explicitly, so the isolated home never
        // becomes the install target and the existing managed install keeps being reused.
        Assert.Contains($"+force_install_dir \"{installDirectory}\"", startInfo.Arguments);
        Assert.Contains("+@sSteamCmdForcePlatformType windows", startInfo.Arguments);
        Assert.Contains("+app_update 298740 +quit", startInfo.Arguments);
        Assert.Equal(steamCmdDirectory, startInfo.WorkingDirectory);
        Assert.False(startInfo.UseShellExecute);
    }

    [Fact]
    public void IsolatedHomeIsSkippedWhenUnsetOrOnWindows()
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo();
        var inherited = new Dictionary<string, string?>(startInfo.Environment);

        ManagedDedicatedServerRuntimeResolver.ApplyIsolatedSteamCmdHome(startInfo, string.Empty);
        Assert.Equal(inherited, startInfo.Environment);

        if (!OperatingSystem.IsWindows())
            return;

        // SteamCMD on Windows does not use HOME; nothing is created or overridden there.
        var homeDirectory = Path.Combine(_root, "SteamCmdHome");
        ManagedDedicatedServerRuntimeResolver.ApplyIsolatedSteamCmdHome(startInfo, homeDirectory);
        Assert.Equal(inherited, startInfo.Environment);
        Assert.False(Directory.Exists(homeDirectory));
    }
}
