using System.Text.Json;
using Magnetar.Protocol.Runtime;
using Quasar.Updates;

// Isolated native-supervisor fixture. Never hosts the Quasar web service or a
// game server. Production shared journal, verifier and replacement code are linked.
if (args.FirstOrDefault() is "serve" or "--installation" or "configure" or "request" or "token-create") return await NativeFixture.RunAsync(args);
var root = Environment.GetEnvironmentVariable("QUASAR_INSTALL_DIR") ?? Environment.CurrentDirectory;
if (args.FirstOrDefault() == "update")
{
    QuasarServiceInstallation.Load(Path.Combine(root, QuasarServiceInstallation.FileName), true);
    return await Quasar.Bootstrap.UpdateCli.RunAsync(args);
}
if (args.FirstOrDefault() == "seal") { await VerifiedUpdatePackage.SealAsync(args[1], CancellationToken.None); return 0; }
if (args.FirstOrDefault() == "apply")
{
    using var lease = await QuasarMaintenanceLease.AcquireAsync(CancellationToken.None);
    LauncherReplacement.Apply(args[1], root);
    return 0;
}
if (args.FirstOrDefault() == "restore") { LauncherReplacement.Restore(root); return 0; }
if (args.FirstOrDefault() == "probe")
{
    var capability = await BootstrapUpdateSupport.DetectAsync(root);
    Console.WriteLine(JsonSerializer.Serialize(capability, QuasarUpdateJournal.JsonOptions));
    return capability.CanRestart ? 0 : 4;
}
if (args.FirstOrDefault() == "verify")
{
    var installation = QuasarServiceInstallation.Load(Path.Combine(root, QuasarServiceInstallation.FileName), true)!;
    var main = await NativeQuasarServices.ReadAsync(installation, false, CancellationToken.None);
    var pid = args.Contains("--wrong-pid") ? Environment.ProcessId : main.Pid;
    if (args.Contains("--wrong-account")) installation = installation with { Account = "invalid-quasar-fixture-account" };
    var capability = await NativeQuasarServices.VerifyAsync(installation, pid, CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(capability, QuasarUpdateJournal.JsonOptions));
    return capability.CanRestart ? 0 : 4;
}
if (args.FirstOrDefault() != "self-test") return 2;

var testRoot = Path.Combine(Path.GetTempPath(), "quasar-update-shared-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testRoot);
Environment.SetEnvironmentVariable("QUASAR_INSTALL_DIR", testRoot);
try
{
    using var lease = await QuasarMaintenanceLease.AcquireAsync(CancellationToken.None);
    var first = QuasarUpdateJournal.Submit("1.4.1", false);
    Require(QuasarUpdateJournal.Submit("1.4.1", false).Id == first.Id, "Duplicate intent must reuse operation identity");
    Reject(() => QuasarUpdateJournal.Submit("1.4.2", false), "Concurrent target must be rejected");
    Reject(() => QuasarUpdateJournal.Submit("../evil", false), "Path-like version must be rejected");
    QuasarUpdateJournal.Fail("interrupted download");
    var retry = QuasarUpdateJournal.Submit(null, true);
    Require(retry.Id != first.Id && retry.Version == first.Version, "Retry must have a new fenced identity and the same release");
    QuasarUpdateJournal.Write(retry with { Stage = UpdateStage.Healthy });
    Require(QuasarUpdateJournal.Read()?.Stage == UpdateStage.Healthy, "Outcome survives journal reload");
    Reject(() => QuasarUpdateJournal.Submit(null, true), "Completed operations must not replay");
    var source = Path.Combine(testRoot, "package"); var install = Path.Combine(testRoot, "install");
    Directory.CreateDirectory(source); Directory.CreateDirectory(install);
    foreach (var path in new[] { source, install })
    {
        File.WriteAllText(Path.Combine(path, LauncherReplacement.ManifestName), "Quasar\nlib/runtime.dll\n");
        Directory.CreateDirectory(Path.Combine(path, "lib"));
        File.WriteAllText(Path.Combine(path, "Quasar"), path == source ? "new" : "old");
        File.WriteAllText(Path.Combine(path, "lib/runtime.dll"), path == source ? "new runtime" : "old runtime");
        File.WriteAllText(Path.Combine(path, "appsettings.json"), path == source ? "package defaults" : "admin config");
    }
    await VerifiedUpdatePackage.SealAsync(source, CancellationToken.None);
    await VerifiedUpdatePackage.VerifyAsync(source, CancellationToken.None);
    File.AppendAllText(Path.Combine(source, "lib/runtime.dll"), "damage");
    try { await VerifiedUpdatePackage.VerifyAsync(source, CancellationToken.None); throw new Exception("Seal accepted damage"); } catch (InvalidDataException) { }
    LauncherReplacement.Apply(source, install);
    Require(File.ReadAllText(Path.Combine(install, "Quasar")) == "new", "Launcher replaced");
    Require(File.ReadAllText(Path.Combine(install, "appsettings.json")) == "admin config", "Config preserved");
    Reject(() => LauncherReplacement.Apply(source, install), "Pending replacement cannot be applied twice");
    LauncherReplacement.Restore(install);
    Require(File.ReadAllText(Path.Combine(install, "Quasar")) == "old" && File.ReadAllText(Path.Combine(install, "lib/runtime.dll")) == "old runtime", "Complete previous launcher restored");
    // Exercise the actual OS file-lock behavior on Windows, not a Unix approximation.
    if (OperatingSystem.IsWindows())
    {
        using (File.Open(Path.Combine(install, "lib/runtime.dll"), FileMode.Open, FileAccess.Read, FileShare.Read))
            Reject(() => LauncherReplacement.Apply(source, install), "Windows locked file must fail replacement");
        LauncherReplacement.Restore(install);
        Require(File.ReadAllText(Path.Combine(install, "lib/runtime.dll")) == "old runtime", "Locked-file failure recoverable after release");
    }
    else Console.WriteLine("SKIP Windows file-lock semantics: non-Windows host");
    foreach (var name in new[] { "../escape", "Updates/operation.json", "appsettings.json", "quasar-supervisor.sh" })
        Reject(() => LauncherReplacement.ValidateName(name), "Protected/path-traversing manifest entry");
    Console.WriteLine("PASS shared journal, duplicate/retry fencing, checksums, complete rollback, config and protected-path validation fixtures");
    return 0;
}
finally { Directory.Delete(testRoot, recursive: true); }

static void Require(bool value, string message) { if (!value) throw new Exception(message); }
static void Reject(Action action, string message) { try { action(); } catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or ArgumentException or UnauthorizedAccessException) { return; } throw new Exception(message); }
