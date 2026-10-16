using Quasar.Diagnostics;
using Xunit;

namespace Quasar.Tests;

// The same LinuxLimits.cs is in Pulsar and the cluster registry, these tests cover all copies.
public sealed class LinuxLimitsTests : IDisposable
{
    private readonly string proc = Path.Combine(Path.GetTempPath(), "linux-limits-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(proc))
            Directory.Delete(proc, true);
    }

    private static string Limits(string openFiles, string processes) =>
        "Limit                     Soft Limit           Hard Limit           Units     \n"
        + "Max cpu time              unlimited            unlimited            seconds   \n"
        + $"Max processes             {processes,-20} {processes,-20} processes \n"
        + $"Max open files            {openFiles,-20} 524288               files     \n"
        + "Max pending signals       348286               348286               signals   \n";

    private void FakeProc(string? limits, string? instances, string? watches)
    {
        Directory.CreateDirectory(Path.Combine(proc, "self"));
        Directory.CreateDirectory(Path.Combine(proc, "sys", "fs", "inotify"));
        if (limits is not null)
            File.WriteAllText(Path.Combine(proc, "self", "limits"), limits);
        if (instances is not null)
            File.WriteAllText(Path.Combine(proc, "sys", "fs", "inotify", "max_user_instances"), instances + "\n");
        if (watches is not null)
            File.WriteAllText(Path.Combine(proc, "sys", "fs", "inotify", "max_user_watches"), watches + "\n");
    }

    [Fact]
    public void LimitsAtOrAboveTheMinimumsGiveNoWarning()
    {
        FakeProc(Limits("65536", "unlimited"), "1024", "1048576");
        Assert.Empty(LinuxLimits.Check(proc, "space"));
    }

    [Fact]
    public void EachLimitBelowItsMinimumGivesOneWarningWithTheFix()
    {
        FakeProc(Limits("1024", "4096"), "128", "65536");

        List<string> warnings = LinuxLimits.Check(proc, "space");

        Assert.Equal(4, warnings.Count);
        Assert.StartsWith("Open file limit (ulimit -n, RLIMIT_NOFILE) is 1024, expected at least 65536.", warnings[0]);
        Assert.Contains("\"space - nofile 65536\" to /etc/security/limits.conf", warnings[0]);
        Assert.Contains("LimitNOFILE=65536", warnings[0]);
        Assert.StartsWith("Process limit (ulimit -u, RLIMIT_NPROC) is 4096, expected at least 16384.", warnings[1]);
        Assert.Contains("\"space - nproc 16384\"", warnings[1]);
        Assert.StartsWith("inotify instance limit (fs.inotify.max_user_instances) is 128, expected at least 1024.", warnings[2]);
        Assert.Contains(
            "echo fs.inotify.max_user_instances=1024 | sudo tee -a /etc/sysctl.d/90-inotify.conf && sudo sysctl -p /etc/sysctl.d/90-inotify.conf",
            warnings[2]);
        Assert.StartsWith("inotify watch limit (fs.inotify.max_user_watches) is 65536, expected at least 524288.", warnings[3]);
        Assert.Contains("fs.inotify.max_user_watches=524288", warnings[3]);
    }

    [Fact]
    public void MissingOrUnreadableValuesAreSkipped()
    {
        FakeProc(null, "not a number", null);
        Assert.Empty(LinuxLimits.Check(proc, "space"));
    }

    [Fact]
    public void ParseRlimitReadsTheSoftLimitOfTheNamedRow()
    {
        string limits = Limits("2048", "unlimited");
        Assert.Equal(2048, LinuxLimits.ParseRlimit(limits, "Max open files"));
        Assert.Equal(long.MaxValue, LinuxLimits.ParseRlimit(limits, "Max processes"));
        Assert.Null(LinuxLimits.ParseRlimit(limits, "Max locked memory"));
        Assert.Null(LinuxLimits.ParseRlimit(limits, "Max open"));
    }

    [Fact]
    public void ChecksTheRealProcOnLinux()
    {
        if (!OperatingSystem.IsLinux())
            return;
        // Values differ between machines, it has to read them without throwing
        LinuxLimits.Check();
        Assert.NotNull(LinuxLimits.ParseRlimit(File.ReadAllText("/proc/self/limits"), "Max open files"));
    }
}
