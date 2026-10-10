using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using CometWorks.Diagnostics;
using Quasar.Diagnostics;
using Quasar.Host.Contract.V1;

if (!OperatingSystem.IsWindows()) { Console.WriteLine("Windows native capture requires Windows; build passed on this platform."); return; }
if (args is ["survive"] or ["survive-silent"])
{
    var watch = Stopwatch.StartNew();
    while (!Native.IsDebuggerPresent() && watch.Elapsed < TimeSpan.FromSeconds(10)) Thread.Sleep(50);
    if (!Native.IsDebuggerPresent()) Environment.Exit(2);
    Console.WriteLine("attached");
    while (Native.IsDebuggerPresent()) Thread.Sleep(50);
    if (args[0] == "survive") Console.WriteLine("detached");
    Thread.Sleep(Timeout.Infinite);
    return;
}
if (args is ["monitor", var state])
{
    using var monitorKey = RSA.Create(3072);
    var monitor = new DiagnosticCollector(state, "monitor-fixture");
    await monitor.ApplyPolicyAsync(new(Guid.NewGuid().ToString("N"), true, true, 1, DateTimeOffset.UtcNow.AddSeconds(-1),
        DateTimeOffset.UtcNow.AddMinutes(2), DiagnosticsProtocol.KeyId(monitorKey), monitorKey.ExportSubjectPublicKeyInfoPem(), DateTimeOffset.UtcNow.AddDays(1)), default);
    var childStart = Child("survive-silent");
    string childRun = monitor.Configure(childStart, new(), native: true)!;
    using var child = Process.Start(childStart)!; monitor.Started(childRun, child);
    if (await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)) != "attached")
        throw new Exception("Monitor-death fixture did not attach.");
    Console.WriteLine(child.Id);
    Thread.Sleep(Timeout.Infinite);
    return;
}

string root = Path.Combine(Path.GetTempPath(), "quasar-native-check-" + Guid.NewGuid().ToString("N"));
using var key = RSA.Create(3072);
var policy = new HostDiagnosticPolicy(Guid.NewGuid().ToString("N"), true, true, 1,
    DateTimeOffset.UtcNow.AddSeconds(-1), DateTimeOffset.UtcNow.AddMinutes(2), DiagnosticsProtocol.KeyId(key),
    key.ExportSubjectPublicKeyInfoPem(), DateTimeOffset.UtcNow.AddDays(1));
var collector = new DiagnosticCollector(root, "windows-fixture");
await collector.ApplyPolicyAsync(policy, default);
Process? crash = null, survive = null, monitorProcess = null, orphan = null;
try
{
    if (args is not [var nativeFixture] || !File.Exists(nativeFixture))
        throw new Exception("Pass the compiled native_crash.exe fixture path.");
    var start = new ProcessStartInfo(nativeFixture) { UseShellExecute = false };
    string run = collector.Configure(start, new() { ["role"] = "native-fixture" }, native: true)!;
    crash = Process.Start(start)!;
    collector.Started(run, crash);
    await crash.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90));
    DiagnosticCollector.Exited(run, crash.ExitCode, false);
    string dump = Path.Combine(run, "native.dmp");
    if (!File.Exists(dump)) throw new Exception("Native Windows dump missing: " + File.ReadAllText(Path.Combine(run, "core-unavailable")));
    uint crashThread = 0;
    using (var file = File.OpenRead(dump))
    {
        using var reader = new BinaryReader(file);
        if (reader.ReadUInt32() != 0x504d444d) throw new Exception("Invalid Windows minidump signature.");
        reader.ReadUInt32();
        uint count = reader.ReadUInt32(), table = reader.ReadUInt32();
        if (count > 100 || table + count * 12L > file.Length) throw new Exception("Invalid minidump directory.");
        for (int i = 0; i < count; i++)
        {
            file.Position = table + i * 12L;
            uint type = reader.ReadUInt32(), bytes = reader.ReadUInt32(), position = reader.ReadUInt32();
            if (type != 6) continue; // ExceptionStream.
            if (bytes < 168 || position + (long)bytes > file.Length) throw new Exception("Invalid exception stream.");
            file.Position = position; crashThread = reader.ReadUInt32(); reader.ReadUInt32();
            if (reader.ReadUInt32() != 0xe0421001) throw new Exception("Native exception code missing from dump.");
            file.Position = position + 160;
            uint contextBytes = reader.ReadUInt32(), contextPosition = reader.ReadUInt32();
            if (contextBytes < 1232 || contextPosition + (long)contextBytes > file.Length)
                throw new Exception("Native exception register context missing from dump.");
        }
        if (crashThread == 0) throw new Exception("ExceptionStream missing from dump.");
    }
    await collector.CollectAsync(default);
    var envelope = collector.GetReports().Single(e => e.ContainsDump);
    var report = await IncidentArchive.ValidateAsync(Path.Combine(collector.IncomingDirectory, envelope.ReportId + ".zip"), envelope, key);
    if (report.Context["hostId"] != "windows-fixture") throw new Exception("Dump correlation lost.");
    if (report.Context["nativeThreadId"] != crashThread.ToString() || report.Context["nativeExceptionCode"] != "0xe0421001")
        throw new Exception("Native dump lost fault-thread attribution.");
    Console.WriteLine("Windows native exception -> full minidump -> encrypted validated archive passed.");

    start = Child("survive");
    run = collector.Configure(start, new() { ["role"] = "revocation-fixture" }, native: true)!;
    survive = Process.Start(start)!; collector.Started(run, survive);
    if (await survive.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)) != "attached")
        throw new Exception("Revocation fixture did not attach.");
    collector.Revoke(2);
    string? line = await survive.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
    if (line != "detached" || survive.HasExited || collector.GetReports().Length != 0)
        throw new Exception("Consent withdrawal failed to detach safely and purge evidence.");
    Console.WriteLine("Windows consent withdrawal leaves the child running and purges outbound evidence passed.");

    start = Child("monitor"); start.ArgumentList.Add(Path.Combine(root, "monitor"));
    monitorProcess = Process.Start(start)!;
    int orphanId = int.Parse((await monitorProcess.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)))!);
    orphan = Process.GetProcessById(orphanId);
    monitorProcess.Kill(); await monitorProcess.WaitForExitAsync();
    await Task.Delay(500);
    if (orphan.HasExited) throw new Exception("Debugger process death killed its surviving child.");
    Console.WriteLine("Windows abrupt Host-monitor death leaves the child running passed.");
}
finally
{
    foreach (var process in new[] { crash, survive, monitorProcess, orphan })
        if (process is not null) { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } process.Dispose(); }
    if (Directory.Exists(root)) Directory.Delete(root, true);
}
static ProcessStartInfo Child(string mode)
{
    var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true };
    start.ArgumentList.Add(typeof(Native).Assembly.Location); start.ArgumentList.Add(mode); return start;
}
internal static class Native
{
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsDebuggerPresent();
}
