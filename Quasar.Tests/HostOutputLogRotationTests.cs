extern alias Host;
using System.Diagnostics;
using Xunit;
using Rotation = Host::Quasar.Hosting.OutputLogRotation;

namespace Quasar.Tests;

// SE-1184: the local Host's host.log (Host, Gateway and node output) was rotated only when Quasar launched the
// Host, and grew without a limit while it ran. The writers append through an inherited ">>" redirect.
public sealed class HostOutputLogRotationTests : IDisposable
{
    private const int Cap = 4096;
    private readonly string _root = Directory
        .CreateTempSubdirectory("host-output-rotation-")
        .FullName;

    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void ALogAChildKeepsAppendingToStaysBounded()
    {
        if (!OperatingSystem.IsLinux())
            return;
        string log = Path.Combine(_root, "host.log");
        var start = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
        foreach (
            string argument in new[]
            {
                "-c",
                "i=0; while :; do i=$((i+1)); echo \"line $i\"; sleep 0.001; done >>\"$1\"",
                "sh",
                log,
            }
        )
            start.ArgumentList.Add(argument);
        using var child = Process.Start(start)!;
        try
        {
            int rotations = 0;
            var deadline = Stopwatch.StartNew();
            while (rotations < 6 && deadline.Elapsed < TimeSpan.FromSeconds(30))
            {
                if (Rotation.RotateIfOver(log, Cap, 4))
                    rotations++;
                Thread.Sleep(5);
            }
            Assert.Equal(6, rotations);
            Assert.False(child.HasExited);
            // The child goes on at the start of the emptied file: no hole of NULs where the old content was.
            long before = new FileInfo(log).Length;
            while (
                new FileInfo(log).Length == before && deadline.Elapsed < TimeSpan.FromSeconds(30)
            )
                Thread.Sleep(5);
            Assert.True(new FileInfo(log).Length > before);
            Assert.Equal(
                new[] { "host.log", "host.log.1", "host.log.2", "host.log.3" },
                Directory.GetFiles(_root).Select(Path.GetFileName).Order().ToArray()
            );
            string[] rotated = new[] { 3, 2, 1 }
                .Select(i => File.ReadAllText($"{log}.{i}"))
                .ToArray();
            foreach (string content in rotated.Append(File.ReadAllText(log)))
            {
                Assert.DoesNotContain('\0', content);
                Assert.True(content.Length < 4 * Cap, $"{content.Length} bytes");
            }
            // Oldest first: each rotated file continues the count where the older one stopped.
            int[] counts = rotated
                .SelectMany(content => content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                .Where(line => line.StartsWith("line "))
                .Select(line => int.Parse(line[5..]))
                .ToArray();
            Assert.Equal(counts.Order(), counts);
        }
        finally
        {
            child.Kill();
            child.WaitForExit();
        }
    }

    [Fact]
    public void ASmallLogIsLeftAloneAndALaunchRotationKeepsTheOlderFiles()
    {
        string log = Path.Combine(_root, "host.log");
        File.WriteAllText(log, "small");
        Assert.False(Rotation.RotateIfOver(log, Cap, 4));
        File.WriteAllText(log + ".1", "previous");
        File.WriteAllText(log, new string('x', Cap));
        Assert.True(Rotation.RotateIfOver(log, Cap, 4));
        Assert.Equal("", File.ReadAllText(log));
        Assert.Equal(new string('x', Cap), File.ReadAllText(log + ".1"));
        Assert.Equal("previous", File.ReadAllText(log + ".2"));
    }
}
