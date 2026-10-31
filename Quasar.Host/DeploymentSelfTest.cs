using System.Text.Json;
using System.Text.Json.Nodes;
using Admin = CometWorks.ClusterGateway.AdminContract.V1;
using HostContract = global::Quasar.Host.Contract.V1;

namespace Quasar.Host;

internal static class DeploymentSelfTest
{
    internal static void Run()
    {
        CredentialInstallation();
        LocalWorldModsRequired();
        ProfileContentUpdate();
        WorldFilesUpdate();
        ModStorageSeeding();
        ConfigurationReferenceRetention();
        SteamClientLibraryInstallation();
        OnlineSnapshot();
        ModLinksAndLeanSnapshots();
        ClusterDetach();
        Assert(HostShutdown.ParseSystemdSeconds("2min") == 120 && HostShutdown.ParseSystemdSeconds("1min 30s") == 90
            && HostShutdown.ParseSystemdSeconds("500ms\n") == 0.5 && HostShutdown.ParseSystemdSeconds("infinity") is null
            && HostShutdown.ParseSystemdSeconds("5s junk") is null, "systemd time span parsing");
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
            VerifiedHashCache(root);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    // #1397: a node's mods are hard links to the read-only seed (a store object in production); a snapshot leaves
    // them out with the handover-diff dumps, and activating the restored deployment writes them again.
    private static void ModLinksAndLeanSnapshots()
    {
        if (!OperatingSystem.IsLinux()) return;
        string root = Path.Combine(Path.GetTempPath(), "host-mod-links-" + Guid.NewGuid().ToString("N"));
        string runtime = Path.Combine(root, "runtime"), state = Path.Combine(root, "state"), slot = "node-1";
        string data = Path.Combine(runtime, "nodes", ExecutionBundle.Hash(System.Text.Encoding.UTF8.GetBytes(slot))[..24], "data");
        string variable = "QUASAR_TEST_MOD_LINKS_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable + "_OLD", "old-fixture-token");
        Environment.SetEnvironmentVariable(variable + "_NEW", "new-fixture-token");
        const string Settings = "<MyObjectBuilder_Checkpoint><Settings><SyncDistance>3000</SyncDistance></Settings>"
            + "<Mods><ModItem><Name>111.sbm</Name><PublishedFileId>111</PublishedFileId></ModItem></Mods></MyObjectBuilder_Checkpoint>";
        try
        {
            (string Path, string Hash) Bundle(string revision, bool pinsMods = true)
            {
                string config = Path.Combine(root, revision);
                var files = new Dictionary<string, string>
                {
                    ["seed/world/Sandbox.sbc"] = Settings, [$"seed/{slot}/ClusterNode.cfg"] = "node settings",
                    [$"seed/{slot}/Mods/111.sbm/Data.sbc"] = "mod payload",
                };
                foreach (var (relative, text) in files)
                {
                    string file = Path.Combine(config, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    File.WriteAllText(file, text);
                }
                // Production seeds reference read-only store objects (ClusterContentStore.AdoptAsync).
                File.SetUnixFileMode(Path.Combine(config, $"seed/{slot}/Mods/111.sbm/Data.sbc"), UnixFileMode.UserRead | UnixFileMode.GroupRead);
                var node = new NodeSpawnSpec(slot, Admin.NodeRole.Regular, slot, "unused", "", [],
                    new() { ["CLUSTER_WORLD_ID"] = "world-1" }, []);
                var manifest = new BundleManifest(1, revision, [], [node], RuntimeRoot: runtime,
                    ConfigFiles: files.Keys.Select(relative => new BundleFile(relative, ExecutionBundle.Hash(File.ReadAllBytes(Path.Combine(config, relative))))).ToArray(),
                    InitialDirectories: new() { [Path.GetRelativePath(runtime, data)] = $"seed/{slot}", ["world"] = "seed/world" },
                    ClusterId: "cluster", HostId: "host",
                    StorageFormats: new() { ["world"] = "test-v1", ["registry"] = "test-v1", ["plugins"] = "test-v1" },
                    ProfileContent: pinsMods ? new("seed/world/Sandbox.sbc", ["111"], new() { [slot] = $"seed/{slot}/Mods" }) : null);
                string path = Path.Combine(config, "bundle.json");
                File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                return (path, ExecutionBundle.Hash(File.ReadAllBytes(path)));
            }
            var first = Bundle("first");
            var attachments = new AttachmentStore(state, []);
            var activation = new DeploymentActivation(state, "host", attachments, new GatewaySpecStore(state),
                new NodeActualizer(state, "host"), new GatewayActualizer(state, "host"));
            var request = new HostContract.HostDeploymentActivation("cluster", null, first.Path, first.Hash,
                "http://127.0.0.1:29416", variable + "_OLD");
            activation.Apply(request);
            ExecutionBundle.Load(first.Path, first.Hash).InitializeData("cluster");
            string mod = Path.Combine(data, "Mods/111.sbm/Data.sbc");
            Assert(File.ReadAllText(mod) == "mod payload" && Quasar.ClusterDeployment.ClusterContentStore.ReferenceCount(mod) == 2,
                "the first seed copied a read-only mod file instead of linking it");
            Assert(Quasar.ClusterDeployment.ClusterContentStore.ReferenceCount(Path.Combine(data, "ClusterNode.cfg")) == 1,
                "the first seed linked a writable node file");
            Directory.CreateDirectory(Path.Combine(data, "cluster/handover-diff"));
            File.WriteAllText(Path.Combine(data, "cluster/handover-diff/7-a-source.xml.gz"), "diagnostic dump");
            File.WriteAllText(Path.Combine(data, "Mods/111.sbm/Data.sbcB5"), "cache the DS wrote");

            var snapshots = new DeploymentSnapshots(state, "host", attachments,
                new NodeActualizer(state, "host"), new GatewayActualizer(state, "host"));
            var saved = snapshots.Capture(new("cluster", Guid.NewGuid(), first.Hash, new string('f', 64)));
            var names = new List<string>();
            using (var reader = new System.Formats.Tar.TarReader(File.OpenRead(snapshots.ArchivePath("cluster", saved.SnapshotId))))
                while (reader.GetNextEntry() is { } entry) names.Add(entry.Name);
            Assert(names.Any(name => name.EndsWith("/data/ClusterNode.cfg", StringComparison.Ordinal))
                && names.Any(name => name.StartsWith("runtime/world/", StringComparison.Ordinal))
                && !names.Any(name => name.Contains("/data/Mods") || name.Contains("handover-diff")),
                "snapshot entries: " + string.Join(", ", names));

            var bare = Bundle("bare", pinsMods: false);
            try
            {
                snapshots.Restore(new("cluster", Guid.NewGuid(), saved.SnapshotId, saved.ArchiveSha256, bare.Path, bare.Hash, variable + "_NEW"), preview: true);
                Assert(false, "a deployment that does not pin the mods restored a snapshot without them");
            }
            catch (InvalidDataException error) when (error.Message.Contains("does not pin")) { }
            var restored = Bundle("restored");
            snapshots.Restore(new("cluster", Guid.NewGuid(), saved.SnapshotId, saved.ArchiveSha256,
                restored.Path, restored.Hash, variable + "_NEW"));
            Assert(!Directory.Exists(Path.Combine(data, "Mods")) && File.ReadAllText(Path.Combine(data, "ClusterNode.cfg")) == "node settings",
                "restore did not take the node data without its mods");
            activation.Apply(request with { ExpectedBundleManifestSha256 = first.Hash, BundleManifestPath = restored.Path,
                BundleManifestSha256 = restored.Hash, ExecutorTokenEnvironmentVariable = variable + "_NEW" });
            Assert(File.ReadAllText(mod) == "mod payload" && Quasar.ClusterDeployment.ClusterContentStore.ReferenceCount(mod) == 2,
                "activating the restored deployment did not link the node's mods again");
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable + "_OLD", null);
            Environment.SetEnvironmentVariable(variable + "_NEW", null);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    // An online snapshot of a running Gateway Host: the registry copy replaces the live database, temporary
    // names, sockets and the export area stay out, and the one-pass archive ends with its manifest.
    private static void OnlineSnapshot()
    {
        string root = Path.Combine(Path.GetTempPath(), "host-online-snapshot-" + Guid.NewGuid());
        string variable = "QUASAR_TEST_ONLINE_EXECUTOR_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, "fixture-token");
        try
        {
            string runtime = Path.Combine(root, "runtime"), state = Path.Combine(root, "state");
            void Write(string relative, string text)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(runtime, relative))!);
                File.WriteAllText(Path.Combine(runtime, relative), text);
            }
            Write("gateway/registry/registry.db", "live");
            Write("gateway/registry/registry.db-wal", "wal");
            Write("gateway/registry/events.jsonl", "events");
            Write("gateway/exports/registry-op1/registry.db", "copy");
            Write("gateway/exports/artifacts.json", "{}");
            Write("gateway/gateway-relay.sock", "socket");
            Write("nodes/one/data/cluster/saves/P-1/g1-s1.mps", "save");
            Write("nodes/one/data/cluster/saves/P-1/.g1-s2.mps.node-1.0123.tmp", "in flight");
            Write("nodes/one/data/cluster/saves/P-1/g1-s3.mps.node-1.part", "partial");
            Write("nodes/one/data/node.log", "log");
            Write("nodes/one/data/cluster/handover-diff/7-a-source.xml.gz", "diagnostic dump");
            Directory.CreateDirectory(Path.Combine(root, "config"));
            string path = Path.Combine(root, "config/bundle.json");
            var manifest = new BundleManifest(1, "revision-1", [], [], new GatewaySpawnSpec("gateway", "{runRoot}", [], []),
                RuntimeRoot: runtime, ClusterId: "cluster", HostId: "host");
            File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            string hash = ExecutionBundle.Hash(File.ReadAllBytes(path));
            var attachments = new AttachmentStore(state, [new("cluster", "http://127.0.0.1:29416", variable, path, hash, runtime)]);
            var snapshots = new DeploymentSnapshots(state, "host", attachments,
                new NodeActualizer(state, "host"), new GatewayActualizer(state, "host"));
            var request = new HostContract.HostSnapshotRequest("cluster", Guid.NewGuid(), hash, new string('f', 64), Online: true,
                RegistryArtifactId: "registry-op1", RegistrySha256: ExecutionBundle.Hash("copy"u8.ToArray()), BytesPerSecond: 1 << 30);
            AssertThrows(() => snapshots.Capture(request with { RegistryArtifactId = null, RegistrySha256 = null }));
            AssertThrows(() => snapshots.Capture(request with { RegistrySha256 = new string('0', 64) }));
            AssertThrows(() => snapshots.Capture(request with { BytesPerSecond = 0 }));
            var saved = snapshots.Capture(request);
            string archive = snapshots.ArchivePath("cluster", saved.SnapshotId);
            var entries = new List<(string Name, byte[] Data)>();
            using (var reader = new System.Formats.Tar.TarReader(File.OpenRead(archive)))
                while (reader.GetNextEntry() is { } entry)
                {
                    using var data = new MemoryStream();
                    entry.DataStream?.CopyTo(data);
                    entries.Add((entry.Name, data.ToArray()));
                }
            var names = entries.Select(e => e.Name).ToHashSet();
            Assert(entries[^1].Name == "snapshot.json", "online snapshot manifest is not last");
            Assert(System.Text.Encoding.UTF8.GetString(entries.Single(e => e.Name == "runtime/gateway/registry/registry.db").Data) == "copy",
                "online snapshot did not take the registry copy");
            Assert(names.SetEquals(["snapshot.json", "runtime/gateway/", "runtime/gateway/registry/", "runtime/gateway/exports/",
                "runtime/nodes/", "runtime/nodes/one/", "runtime/nodes/one/data/", "runtime/nodes/one/data/cluster/",
                "runtime/nodes/one/data/cluster/saves/", "runtime/nodes/one/data/cluster/saves/P-1/",
                "runtime/gateway/registry/registry.db", "runtime/gateway/registry/events.jsonl",
                "runtime/nodes/one/data/cluster/saves/P-1/g1-s1.mps", "runtime/nodes/one/data/node.log"]),
                "online snapshot entries: " + string.Join(", ", names.Order()));
            Assert(saved == snapshots.Capture(request), "online snapshot replay changed archive identity");

            // Archives of earlier Hosts start with a schema 2 manifest; they must stay readable.
            var legacy = JsonNode.Parse(entries[^1].Data)!;
            legacy["schemaVersion"] = 2;
            string old = Path.Combine(root, "legacy.tar");
            using (var output = File.Create(old))
            using (var writer = new System.Formats.Tar.TarWriter(output))
            {
                writer.WriteEntry(new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.RegularFile, "snapshot.json")
                    { DataStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(legacy.ToJsonString())) });
                using var reader = new System.Formats.Tar.TarReader(File.OpenRead(archive));
                while (reader.GetNextEntry() is { } entry)
                    if (entry.Name != "snapshot.json") writer.WriteEntry(entry);
            }
            Assert(snapshots.Describe(old, "cluster", saved.SnapshotId).SnapshotId == saved.SnapshotId, "schema 2 snapshot is unreadable");
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void LocalWorldModsRequired()
    {
        string variable = "QUASAR_TEST_MOD_LOADING_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, "0");
        try
        {
            var attachment = new HostContract.HostAttachmentSpec("cluster", "http://gateway", "TOKEN",
                "/bundle.json", new('a', 64), Path.GetTempPath());
            foreach (var role in new[] { Admin.NodeRole.Regular, Admin.NodeRole.WorldAuthority })
            {
                var plan = new Admin.NodePlan("slot", "host", role, Admin.NodeGoal.Wanted,
                    Admin.NodeObservation.Missing, null, Admin.IncumbentAction.None,
                    null, 0, null, null, null, null, 0, false, true);
                var spec = new NodeSpawnSpec("slot", role, "node", "runtime", "", [],
                    new() { ["CLUSTER_LOCAL_WORLD_MODS"] = "0" }, [],
                    SecretEnvironment: new() { ["CLUSTER_LOCAL_WORLD_MODS"] = variable });
                var start = NodeActualizer.CreateStartInfo(attachment, plan, spec, Path.GetTempPath(),
                    Path.GetTempPath(), "ready", "attempt", "runtime", "revision");
                Assert(start.Environment["CLUSTER_LOCAL_WORLD_MODS"] == "1",
                    "managed node can enable Workshop downloads on recycle");
                var defaultStart = NodeActualizer.CreateStartInfo(attachment, plan,
                    spec with { Environment = [], SecretEnvironment = null }, Path.GetTempPath(),
                    Path.GetTempPath(), "ready", "attempt", "runtime", "revision");
                Assert(defaultStart.Environment["CLUSTER_LOCAL_WORLD_MODS"] == "1",
                    "managed node can download Workshop mods by omitting the setting");
            }
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    private static void ConfigurationReferenceRetention()
    {
        string state = Path.Combine(Path.GetTempPath(), "configuration-retention-" + Guid.NewGuid().ToString("N"));
        string first = Path.Combine(state, "conversions/first/configuration"), next = Path.Combine(state, "conversions/next/configuration");
        string runtime = Path.Combine(state, "conversions/runtime");
        try
        {
            Directory.CreateDirectory(runtime);
            File.WriteAllText(Path.Combine(runtime, "world"), "live world");
            foreach (string directory in new[] { first, next })
            {
                Directory.CreateDirectory(directory);
                File.WriteAllBytes(Path.Combine(directory, "bundle.json"), JsonSerializer.SerializeToUtf8Bytes(
                    new BundleManifest(1, "revision", [], [], RuntimeRoot: runtime, ClusterId: "demo"),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                DeploymentConfigurationReferences.Track(state, "demo", directory);
            }
            DeploymentConfigurationReferences.Retain(state, "demo", [next], [first]);
            Assert(Directory.Exists(first) && Directory.Exists(next), "active configuration was not retained");
            DeploymentConfigurationReferences.Retain(state, "demo", [next], []);
            Assert(!Directory.Exists(first) && Directory.Exists(next), "unused configuration was not pruned");
            DeploymentConfigurationReferences.Retain(state, "demo", [], []);
            Assert(!Directory.Exists(next) && File.ReadAllText(Path.Combine(runtime, "world")) == "live world", "runtime was affected by configuration pruning");
        }
        finally { if (Directory.Exists(state)) Directory.Delete(state, true); }
    }

    // #1313: a staged world seed revision updates the preserved worlds' Storage files at activation, and only those.
    private static void WorldFilesUpdate()
    {
        string root = Path.Combine(Path.GetTempPath(), "host-world-files-" + Guid.NewGuid().ToString("N"));
        string runtime = Path.Combine(root, "runtime");
        const string mes = "Storage/1521905890.sbm_ModularEncountersSystems/Config-General.xml";
        const string other = "Storage/111.sbm_Other/state.dat";
        string[] slots = ["wa", "node-1", "spare-1", "unseeded"];
        string Data(string slot) => Path.Combine(runtime, "nodes", ExecutionBundle.Hash(System.Text.Encoding.UTF8.GetBytes(slot))[..24], "data");
        var worlds = new List<string> { Path.Combine(runtime, "world") };
        foreach (string slot in slots.SkipLast(1))
            worlds.AddRange([Path.Combine(Data(slot), "premade-world"), Path.Combine(Data(slot), "Saves/world-1")]);
        try
        {
            foreach (string world in worlds)
            {
                Directory.CreateDirectory(Path.Combine(world, Path.GetDirectoryName(mes)!));
                Directory.CreateDirectory(Path.Combine(world, Path.GetDirectoryName(other)!));
                File.WriteAllText(Path.Combine(world, "Sandbox.sbc"), "<checkpoint/>");
                File.WriteAllText(Path.Combine(world, mes), "<MaxNpcGrids>100</MaxNpcGrids> rewritten by MES");
                File.WriteAllText(Path.Combine(world, other), "runtime state");
            }
            (BundleManifest Manifest, string Path, string Hash) Bundle(string revision, string mesConfig)
            {
                string config = Path.Combine(root, revision);
                var files = new Dictionary<string, string> { ["seed/world/Sandbox.sbc"] = "<checkpoint/>",
                    ["seed/world/" + mes] = mesConfig, ["seed/world/" + other] = "imported state" };
                foreach (var (name, text) in files)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(config, name))!);
                    File.WriteAllText(Path.Combine(config, name), text);
                }
                var nodes = slots.Select(slot => new NodeSpawnSpec(slot, slot == "wa" ? Admin.NodeRole.WorldAuthority : Admin.NodeRole.Regular,
                    slot, "unused", "", [], new() { ["CLUSTER_WORLD_ID"] = "world-1" }, [])).ToArray();
                var manifest = new BundleManifest(1, revision, [], nodes, RuntimeRoot: runtime,
                    InitialDirectories: new() { ["world"] = "seed/world" },
                    ConfigFiles: files.Keys.Select(f => new BundleFile(f, ExecutionBundle.Hash(File.ReadAllBytes(Path.Combine(config, f))))).ToArray());
                string path = Path.Combine(config, "bundle.json");
                File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                return (manifest, path, ExecutionBundle.Hash(File.ReadAllBytes(path)));
            }
            bool All(string name, string text) => worlds.All(w => File.ReadAllText(Path.Combine(w, name)) == text);
            var old = Bundle("old", "<MaxNpcGrids>100</MaxNpcGrids>");
            var staged = Bundle("staged", "<MaxNpcGrids>40</MaxNpcGrids>");
            var later = Bundle("later", "<MaxNpcGrids>40</MaxNpcGrids>");
            Assert(WorldFilesMigration.Changed(staged.Manifest, null).Length == 0, "a first deployment copied world files");
            Assert(WorldFilesMigration.Changed(staged.Manifest, old.Manifest).SequenceEqual([mes]), "world file diff is wrong");
            var candidate = ExecutionBundle.Load(staged.Path, staged.Hash);
            WorldFilesMigration.Apply(candidate, staged.Path, old.Manifest);
            WorldFilesMigration.Apply(candidate, staged.Path, old.Manifest); // Replayed activation.
            Assert(All(mes, "<MaxNpcGrids>40</MaxNpcGrids>"), "a node or the Gateway world missed the staged world file");
            Assert(All(other, "runtime state"), "an unchanged Storage file was overwritten from the seed");
            Assert(!Directory.Exists(Data("unseeded")), "world files created an unseeded node's data");
            foreach (string world in worlds) File.WriteAllText(Path.Combine(world, mes), "<MaxNpcGrids>40</MaxNpcGrids> rewritten by MES");
            WorldFilesMigration.Apply(ExecutionBundle.Load(later.Path, later.Hash), later.Path, staged.Manifest);
            Assert(All(mes, "<MaxNpcGrids>40</MaxNpcGrids> rewritten by MES"), "a later update with the same seed touched world files");
            File.WriteAllText(Path.Combine(root, "staged", "seed/world", mes), "tampered");
            AssertThrows(() => WorldFilesMigration.Apply(candidate, staged.Path, old.Manifest));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    // #1281: before each node start, the node's mod Storage folders become the World Authority's copy.
    private static void ModStorageSeeding()
    {
        string root = Path.Combine(Path.GetTempPath(), "host-mod-storage-" + Guid.NewGuid().ToString("N"));
        string runtime = Path.Combine(root, "runtime");
        const string mes = "1521905890.sbm_ModularEncountersSystems";
        string Data(string slot) => Path.Combine(runtime, "nodes", ExecutionBundle.Hash(System.Text.Encoding.UTF8.GetBytes(slot))[..24], "data");
        void Write(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
        Dictionary<string, string> Hashes(string directory) => !Directory.Exists(directory) ? [] :
            Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).ToDictionary(
                file => Path.GetRelativePath(directory, file), file => ExecutionBundle.Hash(File.ReadAllBytes(file)));
        bool Same(Dictionary<string, string> left, Dictionary<string, string> right) =>
            left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var hash) && hash == pair.Value);
        try
        {
            var nodes = new[] { "wa", "node-1", "node-2" }.Select(slot => new NodeSpawnSpec(slot,
                slot == "wa" ? Admin.NodeRole.WorldAuthority : Admin.NodeRole.Regular, slot, "unused", "", [],
                new() { ["CLUSTER_WORLD_ID"] = "world-1" }, [])).ToArray();
            var manifest = new BundleManifest(1, "revision", [], nodes, RuntimeRoot: runtime,
                ProfileContent: new ProfileContentManifest("seed/world/Sandbox.sbc", [], []));
            string wa = Data("wa"), node = Data("node-1"), fresh = Data("node-2");
            Write(Path.Combine(wa, "premade-world/Storage", mes, "Config-General.xml"), "seed");
            Write(Path.Combine(wa, "premade-world/Storage", mes, "Config-Grids.xml"), "seed grids");
            Write(Path.Combine(wa, "Saves/world-1/Storage", mes, "Config-General.xml"), "written by the WA");
            Write(Path.Combine(wa, "Storage/111_Local/local.dat"), "WA local");
            Write(Path.Combine(wa, "Storage/global.dat"), "WA global");
            Write(Path.Combine(node, "premade-world/Storage", mes, "Config-General.xml"), "seed");
            Write(Path.Combine(node, "Saves/world-1/Storage", mes, "Config-General.xml"), "written by node-1");
            Write(Path.Combine(node, "Saves/world-1/Storage/222_Node/only-here.dat"), "written by node-1");
            Write(Path.Combine(node, "Storage/global.dat"), "node-1 global");
            Write(Path.Combine(fresh, "premade-world/Sandbox.sbc"), "<checkpoint/>");
            Directory.CreateDirectory(Path.Combine(node, "Saves/world-1/Storage.quasar-previous")); // An interrupted seed.

            ModStorageSeed.Apply(manifest with { ProfileContent = null }, nodes[1]);
            Assert(File.ReadAllText(Path.Combine(node, "Storage/global.dat")) == "node-1 global", "an unmanaged bundle seeded mod Storage");

            foreach (var spec in nodes.Reverse()) ModStorageSeed.Apply(manifest, spec);
            var canonical = new Dictionary<string, string>
            {
                [Path.Combine(mes, "Config-General.xml")] = ExecutionBundle.Hash("written by the WA"u8.ToArray()),
                [Path.Combine(mes, "Config-Grids.xml")] = ExecutionBundle.Hash("seed grids"u8.ToArray()),
            };
            foreach (string world in new[] { Path.Combine(wa, "premade-world"), Path.Combine(wa, "Saves/world-1"),
                         Path.Combine(node, "premade-world"), Path.Combine(node, "Saves/world-1"), Path.Combine(fresh, "premade-world") })
                Assert(Same(Hashes(Path.Combine(world, "Storage")), canonical), "world Storage differs from the WA's: " + world);
            Assert(Same(Hashes(Path.Combine(node, "Storage")), Hashes(Path.Combine(wa, "Storage")))
                && Same(Hashes(Path.Combine(fresh, "Storage")), Hashes(Path.Combine(wa, "Storage"))), "local and global Storage differ from the WA's");
            Assert(Hashes(Path.Combine(wa, "Storage")).Count == 2, "the WA's local and global Storage changed");
            // #1349: a node's mods read world Storage in Saves/<world> already in its first LoadData.
            Assert(Same(Hashes(Path.Combine(fresh, "Saves/world-1/Storage")), canonical)
                && Directory.EnumerateFileSystemEntries(Path.Combine(fresh, "Saves/world-1")).Count() == 1,
                "a node's first start misses the WA's world Storage in its saved world");
            Assert(!Directory.EnumerateFileSystemEntries(node, "*.quasar-*", SearchOption.AllDirectories).Any()
                && !Directory.EnumerateFileSystemEntries(wa, "*.quasar-*", SearchOption.AllDirectories).Any(), "seeding left work folders");

            // A node's own write stays in its process: its next start gets the WA's copy again.
            Write(Path.Combine(node, "Saves/world-1/Storage", mes, "Config-General.xml"), "written by node-1 again");
            Write(Path.Combine(node, "Storage/global.dat"), "node-1 global again");
            ModStorageSeed.Apply(manifest, nodes[1]);
            Assert(Same(Hashes(Path.Combine(node, "Saves/world-1/Storage")), canonical)
                && Same(Hashes(Path.Combine(node, "Storage")), Hashes(Path.Combine(wa, "Storage"))), "a node's write leaked into its next start");

            // A WA without mod Storage leaves none on the node.
            Directory.Delete(Path.Combine(wa, "Storage"), true);
            Directory.Delete(Path.Combine(wa, "premade-world/Storage"), true);
            Directory.Delete(Path.Combine(wa, "Saves/world-1/Storage"), true);
            ModStorageSeed.Apply(manifest, nodes[1]);
            Assert(!Directory.Exists(Path.Combine(node, "Storage")) && !Directory.Exists(Path.Combine(node, "premade-world/Storage"))
                && !Directory.Exists(Path.Combine(node, "Saves/world-1/Storage")), "Storage the WA does not have stayed on a node");
            Directory.Delete(Path.Combine(fresh, "Saves"), true);
            ModStorageSeed.Apply(manifest, nodes[2]);
            Assert(!Directory.Exists(Path.Combine(fresh, "Saves/world-1")), "seeding no Storage created a node's saved world");

            if (!OperatingSystem.IsWindows())
            {
                Write(Path.Combine(wa, "Storage/global.dat"), "WA global");
                Directory.CreateDirectory(Path.Combine(node, "Storage"));
                Directory.CreateSymbolicLink(Path.Combine(node, "Storage/link"), root);
                AssertThrows(() => ModStorageSeed.Apply(manifest, nodes[1]));
                Assert(Directory.Exists(root), "seeding followed a link");
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static void ProfileContentUpdate()
    {
        string root = Path.Combine(Path.GetTempPath(), "host-profile-content-" + Guid.NewGuid().ToString("N"));
        string runtime = Path.Combine(root, "runtime"), slot = "node-1";
        string data = Path.Combine(runtime, "nodes", ExecutionBundle.Hash(System.Text.Encoding.UTF8.GetBytes(slot))[..24], "data");
        static string Checkpoint(string id, string speed) =>
            $"<MyObjectBuilder_Checkpoint><Settings><SyncDistance>{speed}</SyncDistance><Unmodeled>keep</Unmodeled></Settings>" +
            $"<Mods><ModItem><Name>{id}.sbm</Name><PublishedFileId>{id}</PublishedFileId></ModItem></Mods>" +
            "<Entities>preserved</Entities></MyObjectBuilder_Checkpoint>";
        try
        {
            foreach (string directory in new[] { Path.Combine(runtime, "world"),
                         Path.Combine(data, "premade-world"), Path.Combine(data, "Saves/world-1") })
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "Sandbox.sbc"), Checkpoint("111", "3000"));
            }
            File.WriteAllText(Path.Combine(data, ".quasar-seed.json"), "managed");
            Directory.CreateDirectory(Path.Combine(data, "Mods/111.sbm"));
            File.WriteAllText(Path.Combine(data, "Mods/.quasar-profile-mods"), "old");
            File.WriteAllText(Path.Combine(data, "Mods/111.sbm/Data.sbc"), "old mod");
            (string Path, string Hash) Bundle(string revision, string id, string speed, bool legacy = false)
            {
                string config = Path.Combine(root, revision);
                string settings = Path.Combine(config, "seed/world/Sandbox.sbc");
                string mod = Path.Combine(config, $"seed/{slot}/Mods/{id}.sbm/Data.sbc");
                Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
                Directory.CreateDirectory(Path.GetDirectoryName(mod)!);
                File.WriteAllText(settings, Checkpoint(id, speed));
                File.WriteAllText(mod, revision + " mod");
                var node = new NodeSpawnSpec(slot, Admin.NodeRole.Regular, slot, "unused", "", [],
                    new() { ["CLUSTER_WORLD_ID"] = "world-1" }, []);
                var manifest = new BundleManifest(1, revision, [], [node], RuntimeRoot: runtime,
                    ConfigFiles: [new("seed/world/Sandbox.sbc", ExecutionBundle.Hash(File.ReadAllBytes(settings))),
                        new($"seed/{slot}/Mods/{id}.sbm/Data.sbc", ExecutionBundle.Hash(File.ReadAllBytes(mod)))],
                    ProfileContent: legacy ? null : new("seed/world/Sandbox.sbc", [id], new() { [slot] = $"seed/{slot}/Mods" }));
                string path = Path.Combine(config, "bundle.json");
                File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                return (path, ExecutionBundle.Hash(File.ReadAllBytes(path)));
            }
            var old = Bundle("old", "111", "3000");
            var next = Bundle("next", "222", "5000");
            var legacy = Bundle("legacy", "111", "3000", legacy: true);
            var candidate = ExecutionBundle.Load(next.Path, next.Hash);
            ProfileContentMigration.Apply(candidate, next.Path);
            ProfileContentMigration.Apply(candidate, next.Path);
            Assert(File.Exists(Path.Combine(data, "Mods/222.sbm/Data.sbc"))
                && !Directory.Exists(Path.Combine(data, "Mods/111.sbm")), "profile mod payload was not replaced");
            foreach (string directory in new[] { Path.Combine(runtime, "world"),
                         Path.Combine(data, "premade-world"), Path.Combine(data, "Saves/world-1") })
            {
                string updated = File.ReadAllText(Path.Combine(directory, "Sandbox.sbc"));
                Assert(updated.Contains("<SyncDistance>5000</SyncDistance>")
                    && updated.Contains("222.sbm") && updated.Contains("<Entities>preserved</Entities>")
                    && updated.Contains("<Unmodeled>keep</Unmodeled>"),
                    "profile update lost world state or missed settings");
            }
            var legacyBundle = ExecutionBundle.Load(legacy.Path, legacy.Hash);
            AssertThrows(() => ProfileContentMigration.Validate(legacyBundle, legacy.Path));
            AssertThrows(() => ProfileContentMigration.Apply(legacyBundle, legacy.Path));
            ProfileContentMigration.Apply(ExecutionBundle.Load(old.Path, old.Hash), old.Path);
            Assert(File.Exists(Path.Combine(data, "Mods/111.sbm/Data.sbc"))
                && File.ReadAllText(Path.Combine(runtime, "world/Sandbox.sbc")).Contains("<SyncDistance>3000</SyncDistance>"),
                "profile rollback did not restore the previous selection");

            // #1349: a node that stopped before its first save has only the seeded mod Storage in Saves/<world>.
            string saved = Path.Combine(data, "Saves/world-1");
            Directory.Delete(saved, true);
            Directory.CreateDirectory(Path.Combine(saved, "Storage/111.sbm"));
            File.WriteAllText(Path.Combine(saved, "Storage/111.sbm/Config.xml"), "seeded");
            ProfileContentMigration.Apply(candidate, next.Path);
            Assert(Directory.EnumerateFileSystemEntries(saved).Count() == 1
                && File.ReadAllText(Path.Combine(saved, "Storage/111.sbm/Config.xml")) == "seeded", "profile update changed an unsaved world");
            File.WriteAllText(Path.Combine(saved, "SANDBOX_0_0_0_.sbs"), "sectors");
            AssertThrows(() => ProfileContentMigration.Apply(candidate, next.Path));
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
        string generation = Guid.NewGuid().ToString("N");
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
            var rotated = request with { Generation = generation, AdminToken = new('e', 64), JoinToken = new('f', 64),
                ExecutorTokens = new() { ["one"] = new('1', 64), ["two"] = new('2', 64) } };
            HostCredentials.Install(directory, rotated);
            HostCredentials.Install(directory, rotated);
            Assert(File.Exists(Path.Combine(directory, "credentials", cluster, generation, "tokens.json")),
                "restore credential roster was not isolated from active tokens");
            Assert(Environment.GetEnvironmentVariable(Ref("restore:" + generation + ":join")) == rotated.JoinToken,
                "restore credential reference was not installed");
            AssertThrows(() => HostCredentials.Install(directory, rotated with { JoinToken = new('9', 64) }));
            var start = new System.Diagnostics.ProcessStartInfo();
            ExecutionBundle.ApplySecrets(start, new() { ["CLUSTER_JOIN_TOKEN"] = Ref("join") });
            Assert(start.Environment["CLUSTER_JOIN_TOKEN"] == request.JoinToken, "required child credential missing");
            Assert(!start.Environment.Keys.Any(name => name.StartsWith("QSR_MANAGED_", StringComparison.Ordinal)), "Host credential inherited by child");
        }
        finally
        {
            foreach (string purpose in new[] { "admin", "join", "executor:one", "executor:two", "token-file" })
                Environment.SetEnvironmentVariable(Ref(purpose), null);
            foreach (string purpose in new[] { "admin", "join", "executor:one", "executor:two", "token-file" })
                Environment.SetEnvironmentVariable(Ref("restore:" + generation + ":" + purpose), null);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    // A verified file is not hashed again until its size or modification time changes.
    private static void VerifiedHashCache(string root)
    {
        string file = Path.Combine(root, "cached.bin");
        File.WriteAllText(file, "first");
        DateTime written = File.GetLastWriteTimeUtc(file);
        string pin = ExecutionBundle.Hash("first"u8.ToArray());
        Assert(ExecutionBundle.VerifiedHash(file, pin.ToUpperInvariant()) == pin, "verified hash mismatch");
        // Same size and time: served from the cache, so this content change is not seen.
        File.WriteAllText(file, "other");
        File.SetLastWriteTimeUtc(file, written);
        Assert(ExecutionBundle.VerifiedHash(file, pin) == pin, "verified hash was not cached");
        File.SetLastWriteTimeUtc(file, written.AddSeconds(1));
        Assert(ExecutionBundle.VerifiedHash(file, pin) is null, "changed modification time did not invalidate the cache");
        Assert(ExecutionBundle.VerifiedHash(file, pin) is null, "failed verification was cached");
        File.WriteAllText(file, "first");
        File.SetLastWriteTimeUtc(file, written);
        Assert(ExecutionBundle.VerifiedHash(file, pin) == pin, "restored file did not verify");
        File.WriteAllText(file, "first!");
        File.SetLastWriteTimeUtc(file, written);
        Assert(ExecutionBundle.VerifiedHash(file, pin) is null, "changed size did not invalidate the cache");
        if (!OperatingSystem.IsLinux()) return;
        // A hard link is the same file: verifying one path covers the others.
        File.WriteAllText(file, "first");
        Assert(ExecutionBundle.VerifiedHash(file, pin) == pin, "verified hash mismatch before linking");
        written = File.GetLastWriteTimeUtc(file);
        string linked = Path.Combine(root, "cached-link.bin");
        Assert(Quasar.ClusterDeployment.ClusterContentStore.Link(file, linked) == 0, "hard link failed");
        File.WriteAllText(file, "other");
        File.SetLastWriteTimeUtc(file, written);
        Assert(ExecutionBundle.VerifiedHash(linked, pin) == pin, "hard link was hashed again");
        File.Delete(linked);
    }

    // A deleted cluster leaves the Host's supervision loop only through the fenced detach (DF-1451).
    private static void ClusterDetach()
    {
        string root = Path.Combine(Path.GetTempPath(), "host-detach-" + Guid.NewGuid());
        try
        {
            string state = Path.Combine(root, "state"), runtime = Path.Combine(root, "runtime");
            Directory.CreateDirectory(Path.Combine(root, "config"));
            string path = Path.Combine(root, "config/bundle.json");
            var manifest = new BundleManifest(1, "revision-1", [], [], new GatewaySpawnSpec("gateway", "{runRoot}", [], [],
                ReservedPorts: [29416]), RuntimeRoot: runtime, ClusterId: "cluster", HostId: "host",
                StorageFormats: new() { ["world"] = "test-v1", ["registry"] = "test-v1", ["plugins"] = "test-v1" });
            File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            string hash = ExecutionBundle.Hash(File.ReadAllBytes(path));
            var attachments = new AttachmentStore(state, [new("configured", "http://127.0.0.1:29417", "EXECUTOR_TOKEN")]);
            var gateways = new GatewaySpecStore(state);
            var activation = new DeploymentActivation(state, "host", attachments, gateways,
                new NodeActualizer(state, "host"), new GatewayActualizer(state, "host"));
            activation.Apply(new("cluster", null, path, hash, "http://127.0.0.1:29416", "EXECUTOR_TOKEN"));
            string world = Path.Combine(runtime, "nodes/one/data/world.dat");
            Directory.CreateDirectory(Path.GetDirectoryName(world)!);
            File.WriteAllText(world, "world");

            AssertThrows(() => activation.Detach("cluster", new string('0', 64)));
            AssertThrows(() => activation.Detach("cluster", ""));
            AssertThrows(() => activation.Detach("configured", ""));
            var spec = gateways.GetAll().Single();
            gateways.Apply(spec with { Goal = HostContract.GatewayGoal.On });
            AssertThrows(() => activation.Detach("cluster", hash));
            gateways.Apply(spec);
            string launch = Path.Combine(state, "launch-records/launching.json");
            Directory.CreateDirectory(Path.GetDirectoryName(launch)!);
            File.WriteAllBytes(launch, JsonSerializer.SerializeToUtf8Bytes(new LaunchRecord(1, "cluster", "slot", "attempt", null, null,
                null, "revision-1", hash, "unused", "unused", runtime, null, null, DateTimeOffset.UtcNow, LaunchStatus.Launching, null),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            AssertThrows(() => activation.Detach("cluster", hash));
            File.Delete(launch);
            string transaction = Directory.GetFiles(Path.Combine(state, "deployments"), "*.json").Single();
            string applied = File.ReadAllText(transaction);
            var json = JsonNode.Parse(applied)!;
            json["applied"] = false;
            File.WriteAllText(transaction, json.ToJsonString());
            AssertThrows(() => activation.Detach("cluster", hash));
            File.WriteAllText(transaction, applied);
            string journal = Path.Combine(state, "restores", Guid.NewGuid().ToString("N") + ".json");
            Directory.CreateDirectory(Path.GetDirectoryName(journal)!);
            File.WriteAllBytes(journal, JsonSerializer.SerializeToUtf8Bytes(new HostContract.HostSnapshotRestore("cluster",
                Guid.NewGuid(), Guid.NewGuid(), hash, path, hash, "EXECUTOR_TOKEN"), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            var snapshots = new DeploymentSnapshots(state, "host", attachments, new NodeActualizer(state, "host"), new GatewayActualizer(state, "host"));
            AssertThrows(() => snapshots.RefuseDuringRestore("CLUSTER"));
            File.Delete(journal);
            snapshots.RefuseDuringRestore("cluster");

            var detached = activation.Detach("CLUSTER", hash.ToUpperInvariant());
            Assert(detached.Detached.SequenceEqual(["attachment", "gateway", "deployment"]), "detach moved " + string.Join(",", detached.Detached));
            Assert(attachments.GetAll().Select(a => a.ClusterId).SequenceEqual(["configured"]) && gateways.GetAll().Length == 0
                && gateways.GetStatuses().Length == 0 && !activation.IsManaged("cluster"), "detached cluster is still supervised");
            Assert(File.Exists(Path.Combine(detached.ArchiveDirectory!, "attachment.json"))
                && File.Exists(Path.Combine(detached.ArchiveDirectory!, "gateway.json"))
                && File.Exists(Path.Combine(detached.ArchiveDirectory!, "deployment.json")), "detach did not keep the state files");
            Assert(File.ReadAllText(world) == "world", "detach touched runtime data");
            // A restart neither reloads nor replays the detached cluster.
            var reloaded = new AttachmentStore(state, []);
            var reloadedGateways = new GatewaySpecStore(state);
            new DeploymentActivation(state, "host", reloaded, reloadedGateways, new NodeActualizer(state, "host"),
                new GatewayActualizer(state, "host")).Recover();
            Assert(reloaded.GetAll().Length == 0 && reloadedGateways.GetAll().Length == 0, "restart brought the detached cluster back");
            var repeated = activation.Detach("cluster", "");
            Assert(repeated.Detached.Length == 0 && repeated.ArchiveDirectory is null
                && Directory.GetDirectories(Path.Combine(state, "detached")).Length == 1, "repeated detach was not a no-op");
            try { activation.Detach("../cluster", ""); throw new InvalidOperationException("detach accepted a path as cluster ID"); }
            catch (ArgumentException) { }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
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
