using System.Text.Json;
using System.Text.Json.Nodes;
using HostContract = global::Quasar.Host.Contract.V1;

namespace Quasar.Host;

internal static class DeploymentSelfTest
{
    internal static void Run()
    {
        CredentialInstallation();
        SteamClientLibraryInstallation();
        string root = Path.Combine(Path.GetTempPath(), "host-deployment-" + Guid.NewGuid());
        try
        {
            string config = Path.Combine(root, "config");
            string runtime = Path.Combine(root, "runtime");
            string state = Path.Combine(root, "state");
            Directory.CreateDirectory(Path.Combine(config, "seed"));
            File.WriteAllText(Path.Combine(config, "seed/world.dat"), "original world");
            var manifest = new BundleManifest(1, "revision-1", [], [], RuntimeRoot: runtime,
                ConfigFiles: [new("seed/world.dat", ExecutionBundle.Hash(File.ReadAllBytes(Path.Combine(config, "seed/world.dat"))))],
                InitialDirectories: new() { ["world"] = "seed" }, ClusterId: "cluster", HostId: "host",
                StorageFormats: new() { ["world"] = "test-v1", ["registry"] = "test-v1", ["plugins"] = "test-v1" });
            AssertThrows(() => ExecutionBundle.RequireCompatibleStorage(null, manifest.StorageFormats));
            AssertThrows(() => ExecutionBundle.RequireCompatibleStorage(new() { ["world"] = "test-v2" }, manifest.StorageFormats));
            string path = Path.Combine(config, "bundle.json");
            File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            string hash = ExecutionBundle.Hash(File.ReadAllBytes(path));
            // Invalid state files are ignored with a message; they must not abort the Host.
            Directory.CreateDirectory(Path.Combine(state, "attachments"));
            Directory.CreateDirectory(Path.Combine(state, "gateways"));
            File.WriteAllText(Path.Combine(state, "attachments/invalid.json"), "{\"clusterId\":\"\",\"gatewayUrl\":\"nowhere\"}");
            File.WriteAllBytes(Path.Combine(state, "gateways/torn.json"), []);
            var attachments = new AttachmentStore(state, []);
            var gateways = new GatewaySpecStore(state);
            Assert(attachments.GetAll().Length == 0 && gateways.GetAll().Length == 0, "invalid state file was loaded");
            File.Delete(Path.Combine(state, "attachments/invalid.json"));
            File.Delete(Path.Combine(state, "gateways/torn.json"));
            var activation = new DeploymentActivation(state, "host", attachments, gateways,
                new NodeActualizer(state, "host"), new GatewayActualizer(state, "host"));
            var request = new HostContract.HostDeploymentActivation("cluster", null, path, hash,
                "http://127.0.0.1:29416", "EXECUTOR_TOKEN");
            var active = activation.Apply(request);
            Assert(activation.CheckRecovery("cluster", hash).HostId == "host", "wrong recovery Host");
            AssertThrows(() => activation.CheckRecovery("cluster", "wrong"));
            string pendingLaunch = Path.Combine(state, "launch-records/pending.json");
            Directory.CreateDirectory(Path.GetDirectoryName(pendingLaunch)!);
            var pending = new LaunchRecord(1, "cluster", "slot", "attempt", null, null, null, "revision-1", hash,
                "unused", "unused", runtime, null, null, DateTimeOffset.UtcNow, LaunchStatus.Launching, null);
            File.WriteAllBytes(pendingLaunch, JsonSerializer.SerializeToUtf8Bytes(pending,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            AssertThrows(() => activation.CheckRecovery("cluster", hash));
            // A final-state record whose PID now belongs to an unrelated live process (this one) proves nothing is running.
            void WriteLaunch(LaunchRecord record) => File.WriteAllBytes(pendingLaunch,
                JsonSerializer.SerializeToUtf8Bytes(record, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            var reused = pending with { ProcessId = Environment.ProcessId, ProcessStartedAt = DateTimeOffset.UtcNow.AddDays(-1) };
            foreach (var final in new[] { LaunchStatus.Gone, LaunchStatus.Failed })
            {
                WriteLaunch(reused with { Status = final });
                Assert(activation.CheckRecovery("cluster", hash).HostId == "host", "reused PID of a final launch record blocked recovery");
            }
            if (OperatingSystem.IsLinux())
            {
                string identity = ProcessIdentity.Capture(Environment.ProcessId) ?? throw new InvalidOperationException("process identity unavailable");
                Assert(ProcessIdentity.Matches(identity, Environment.ProcessId) == true, "own process identity did not match");
                // Same PID, another boot or start tick: the recorded process is gone, not an unmanaged conflict.
                WriteLaunch(reused with { Status = LaunchStatus.Running, ProcessIdentity = "00000000-0000-0000-0000-000000000000/1" });
                Assert(activation.CheckRecovery("cluster", hash).HostId == "host", "reused PID after reboot blocked recovery");
                WriteLaunch(reused with { Status = LaunchStatus.Running, ProcessIdentity = identity[..(identity.IndexOf('/') + 1)] + "1" });
                Assert(activation.CheckRecovery("cluster", hash).HostId == "host", "reused PID within one boot blocked recovery");
                // A matching identity is a live process even when the wall clock moved; recovery stays blocked.
                WriteLaunch(reused with { Status = LaunchStatus.Running, ProcessIdentity = identity });
                AssertThrows(() => activation.CheckRecovery("cluster", hash));
            }
            File.Delete(pendingLaunch);
            Assert(active == activation.Apply(request), "activation replay changed identity");
            Assert(!Directory.Exists(runtime), "activation initialized mutable data prematurely");
            AssertThrows(() => activation.Apply(request with { ExpectedBundleManifestSha256 = "wrong" }));
            var bundle = ExecutionBundle.Load(path, hash);
            bundle.InitializeData("cluster");
            string world = Path.Combine(runtime, "world/world.dat");
            File.WriteAllText(world, "saved world");
            bundle.InitializeData("cluster");
            Assert(File.ReadAllText(world) == "saved world", "warm seed replay overwrote saved data");
            // Emulate a crash after intent/attachment write, before the completed marker.
            string transaction = Directory.GetFiles(Path.Combine(state, "deployments"), "*.json").Single();
            var json = JsonNode.Parse(File.ReadAllText(transaction))!;
            json["applied"] = false;
            File.WriteAllText(transaction, json.ToJsonString());
            activation.Recover();
            Assert(JsonNode.Parse(File.ReadAllText(transaction))!["applied"]!.GetValue<bool>(), "pending activation was not recovered");
            string slot = Path.Combine(runtime, "slot");
            Directory.CreateDirectory(slot);
            Directory.CreateDirectory(Path.Combine(config, "plugin-config"));
            File.WriteAllText(Path.Combine(config, "plugin-config/loader.json"), "first");
            var node = new NodeSpawnSpec("regular", CometWorks.ClusterGateway.AdminContract.V1.NodeRole.Regular,
                "node", "unused", "unused", [], [], [], ConfigurationSeed: "plugin-config");
            var configManifest = manifest with { ConfigFiles = [new("plugin-config/loader.json", ExecutionBundle.Hash("first"u8.ToArray()))] };
            ExecutionBundle.PrepareNodeConfiguration(path, configManifest, node, slot, "cluster");
            string privateFile = Path.Combine(slot, "config/plugin-state.db");
            File.WriteAllText(privateFile, "durable plugin state");
            Directory.CreateDirectory(Path.Combine(slot, "config/empty-private"));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(privateFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.WriteAllText(Path.Combine(config, "plugin-config/loader.json"), "second");
            configManifest = configManifest with { ConfigFiles = [new("plugin-config/loader.json", ExecutionBundle.Hash("second"u8.ToArray()))] };
            ExecutionBundle.PrepareNodeConfiguration(path, configManifest, node, slot, "cluster");
            Assert(File.ReadAllText(privateFile) == "durable plugin state", "deployment discarded private plugin state");
            Assert(File.ReadAllText(Path.Combine(slot, "config/loader.json")) == "second", "deployment did not update loader config");
            // Crash between the two directory renames must retain plugin-owned state.
            Directory.Move(Path.Combine(slot, "config"), Path.Combine(slot, "config.previous"));
            ExecutionBundle.PrepareNodeConfiguration(path, configManifest, node, slot, "cluster");
            Assert(File.ReadAllText(privateFile) == "durable plugin state", "configuration recovery discarded private state");
            Environment.SetEnvironmentVariable("EXECUTOR_TOKEN", "old-fixture-token");
            Environment.SetEnvironmentVariable("RESTORED_EXECUTOR_TOKEN", "new-fixture-token");
            try
            {
                var snapshots = new DeploymentSnapshots(state, "host", attachments,
                    new NodeActualizer(state, "host"), new GatewayActualizer(state, "host"));
                var saved = snapshots.Capture(new("cluster", Guid.NewGuid(), hash, new string('f', 64)));
                Assert(saved == snapshots.Capture(new("cluster", saved.SnapshotId, hash, new string('f', 64))), "snapshot replay changed archive identity");
                AssertThrows(() => snapshots.Capture(new("cluster", saved.SnapshotId, hash, new string('e', 64))));
                File.WriteAllText(privateFile, "new state after backup");
                string candidatePath = Path.Combine(config, "candidate.json");
                File.WriteAllBytes(candidatePath, JsonSerializer.SerializeToUtf8Bytes(manifest with { Revision = "restored" }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                string candidateHash = ExecutionBundle.Hash(File.ReadAllBytes(candidatePath));
                var restore = new HostContract.HostSnapshotRestore("cluster", Guid.NewGuid(), saved.SnapshotId,
                    saved.ArchiveSha256, candidatePath, candidateHash, "RESTORED_EXECUTOR_TOKEN");
                AssertThrows(() => snapshots.Restore(restore with { CandidateExecutorTokenEnvironmentVariable = "EXECUTOR_TOKEN" }));
                AssertThrows(() => snapshots.Restore(restore with {
                    CandidateManifestPath = path, CandidateManifestSha256 = hash }));
                Assert(File.ReadAllText(privateFile) == "new state after backup", "rejected restore mutated runtime data");
                snapshots.Restore(restore, preview: true);
                Assert(File.ReadAllText(privateFile) == "new state after backup", "restore preview mutated runtime data");
                snapshots.Restore(restore);
                Assert(File.ReadAllText(privateFile) == "durable plugin state", "restore lost private plugin state");
                Assert(Directory.Exists(Path.Combine(slot, "config/empty-private")), "restore lost empty plugin directory");
                if (!OperatingSystem.IsWindows()) Assert(File.GetUnixFileMode(privateFile) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "restore changed private file permissions");
                AssertThrows(() => bundle.InitializeData("cluster"));
                var restored = activation.Apply(request with { ExpectedBundleManifestSha256 = hash,
                    BundleManifestPath = candidatePath, BundleManifestSha256 = candidateHash, ExecutorTokenEnvironmentVariable = "RESTORED_EXECUTOR_TOKEN" });
                snapshots.Restore(restore); // Response-loss replay, even after activation.
                ExecutionBundle.Load(candidatePath, candidateHash).InitializeData("cluster");
                Assert(Directory.Exists(runtime + ".before-restore-" + restore.RestoreId.ToString("N")), "restore discarded previous runtime");
            }
            finally
            {
                Environment.SetEnvironmentVariable("EXECUTOR_TOKEN", null);
                Environment.SetEnvironmentVariable("RESTORED_EXECUTOR_TOKEN", null);
            }
            File.WriteAllText(Path.Combine(config, "seed/world.dat"), "tampered seed");
            AssertThrows(() => ExecutionBundle.Load(path, hash));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static void SteamClientLibraryInstallation()
    {
        string home = Path.Combine(Path.GetTempPath(), "host-steam-client-" + Guid.NewGuid().ToString("N"));
        try
        {
            byte[] library = "steamclient"u8.ToArray();
            string hash = ExecutionBundle.Hash(library);
            var first = SteamClientLibrary.InstallAsync(home, hash, new MemoryStream(library), default).GetAwaiter().GetResult();
            Assert(first.Installed && first.Path == Path.Combine(home, ".steam/sdk64/steamclient.so")
                && File.ReadAllBytes(first.Path).SequenceEqual(library), "steamclient.so was not staged under ~/.steam/sdk64");
            var replay = SteamClientLibrary.InstallAsync(home, hash, new MemoryStream(library), default).GetAwaiter().GetResult();
            Assert(!replay.Installed, "identical steamclient.so was written again");
            AssertThrows(() => SteamClientLibrary.InstallAsync(home, ExecutionBundle.Hash("other"u8.ToArray()), new MemoryStream(library), default).GetAwaiter().GetResult());
            Assert(File.ReadAllBytes(first.Path).SequenceEqual(library) && Directory.GetFiles(Path.GetDirectoryName(first.Path)!).Length == 1,
                "a rejected upload changed steamclient.so or left a staging file");
        }
        finally { if (Directory.Exists(home)) Directory.Delete(home, true); }
    }

    private static void CredentialInstallation()
    {
        string directory = Path.Combine(Path.GetTempPath(), "host-credentials-" + Guid.NewGuid().ToString("N"));
        string cluster = "test-" + Guid.NewGuid().ToString("N");
        string Ref(string purpose) => HostContract.ManagedCredentialReference.Cluster(cluster, purpose);
        try
        {
            Directory.CreateDirectory(directory);
            var request = new HostContract.HostManagedCredentials(cluster, new('a', 64), new('b', 64),
                new() { ["one"] = new('c', 64), ["two"] = new('d', 64) });
            HostCredentials.Install(directory, request);
            byte[] original = File.ReadAllBytes(Path.Combine(directory, "credentials.json"));
            string tokenFile = Path.Combine(directory, "credentials", cluster, "tokens.json");
            string[] Names() => System.Text.Json.JsonDocument.Parse(File.ReadAllText(tokenFile)).RootElement.GetProperty("tokens")
                .EnumerateArray().Select(t => t.GetProperty("name").GetString()!).ToArray();
            Assert(Names().SequenceEqual(["one", "two"]), "executor token name differs from Host ID");
            File.WriteAllText(tokenFile, File.ReadAllText(tokenFile).Replace("\"name\":\"", "\"name\":\"host-"));
            HostCredentials.Install(directory, request);
            Assert(Names().SequenceEqual(["one", "two"]), "legacy prefixed executor roster was not migrated");
            HostCredentials.Install(directory, request);
            Assert(original.SequenceEqual(File.ReadAllBytes(Path.Combine(directory, "credentials.json"))), "credential replay changed file");
            AssertThrows(() => HostCredentials.Install(directory, request with { ExecutorTokens = new() { ["one"] = new('c', 64) } }));
            AssertThrows(() => HostCredentials.Install(directory, request with { AdminToken = new('f', 64) }));
            var start = new System.Diagnostics.ProcessStartInfo();
            ExecutionBundle.ApplySecrets(start, new() { ["CLUSTER_JOIN_TOKEN"] = Ref("join") });
            Assert(start.Environment["CLUSTER_JOIN_TOKEN"] == request.JoinToken, "required child credential missing");
            Assert(!start.Environment.Keys.Any(name => name.StartsWith("QSR_MANAGED_", StringComparison.Ordinal)), "Host credential inherited by child");
        }
        finally
        {
            foreach (string purpose in new[] { "admin", "join", "executor:one", "executor:two", "token-file" })
                Environment.SetEnvironmentVariable(Ref(purpose), null);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static void Assert(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static void AssertThrows(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is InvalidOperationException or InvalidDataException or System.Security.Cryptography.CryptographicException) { return; }
        throw new InvalidOperationException("Expected deployment rejection.");
    }
}
