using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using Magnetar.Protocol.Model;
using Newtonsoft.Json;
using Sandbox;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using VRage.Game.ModAPI;
using VRage.GameServices;
using VRage.Network;

namespace Quasar.Agent
{
    /// <summary>Standalone admission uses only local, expiring decisions; never network I/O on a join path.</summary>
    internal sealed class PlayerAccessEnforcement : IDisposable
    {
        private const string HarmonyId = "quasar.agent.player-access";
        private static PlayerAccessEnforcement current;
        private readonly string target;
        private readonly string markerPath;
        private readonly string promotionsPath;
        private readonly Harmony harmony;
        private readonly Dictionary<ulong, ManagedPromotion> managed;
        private PlayerAccessSnapshot snapshot;
        private Dictionary<ulong, PlayerAccessGrant> grants = new Dictionary<ulong, PlayerAccessGrant>();
        private bool whitelist;
        private long requiredRevision;
        private DateTime lastUpdate;
        private MySession promotionSource;
        private bool applyingPromotion;
        public bool Ready { get; private set; }

        internal PlayerAccessEnforcement(string uniqueName)
        {
            target = "server:" + uniqueName;
            var directory = Environment.GetEnvironmentVariable("QUASAR_DS_APPDATA_PATH");
            if (string.IsNullOrEmpty(directory)) return;
            string suffix;
            using (var hash = SHA256.Create()) suffix = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(target))).Replace("-", "");
            markerPath = Path.Combine(directory, ".quasar-player-access-" + suffix + ".json");
            promotionsPath = Path.Combine(directory, ".quasar-player-promotions-" + suffix + ".json");
            var required = Environment.GetEnvironmentVariable("QUASAR_PLAYER_WHITELIST");
            var marker = File.Exists(markerPath) ? JsonConvert.DeserializeObject<AccessMarker>(File.ReadAllText(markerPath))
                ?? throw new InvalidDataException("Invalid player whitelist marker.") : new AccessMarker();
            if (marker.Revision < 0) throw new InvalidDataException("Invalid player whitelist revision.");
            long.TryParse(Environment.GetEnvironmentVariable("QUASAR_PLAYER_ACCESS_REVISION"), out var launchRevision);
            whitelist = InitialWhitelist(required is null ? (bool?)null : required == "true", launchRevision, marker.Whitelist, marker.Revision);
            requiredRevision = Math.Max(marker.Revision, launchRevision);
            if (required is not null && launchRevision >= marker.Revision)
                WriteAtomic(markerPath, JsonConvert.SerializeObject(new AccessMarker { Whitelist = whitelist, Revision = requiredRevision }));
            managed = File.Exists(promotionsPath)
                ? JsonConvert.DeserializeObject<Dictionary<ulong, ManagedPromotion>>(File.ReadAllText(promotionsPath))
                    ?? throw new InvalidDataException("Invalid managed player promotion journal.")
                : new Dictionary<ulong, ManagedPromotion>();
            if (managed.Count > 10000 || managed.Any(p => p.Key == 0 || p.Value.Baseline < 0 || p.Value.Baseline > 4
                || p.Value.Applied.Any(level => level < 0 || level > 4)))
                throw new InvalidDataException("Invalid managed player promotion journal.");
            current = this;
            harmony = new Harmony(HarmonyId);
            try
            {
                harmony.CreateClassProcessor(typeof(AdmissionPatch)).Patch();
                harmony.CreateClassProcessor(typeof(PriorityPatch)).Patch();
                Ready = true;
            }
            catch (Exception error)
            {
                harmony.UnpatchAll(HarmonyId);
                Console.WriteLine("Quasar player admission patches are unavailable: " + error.Message);
                // Init runs before the server starts accepting players. A configured whitelist must never start open.
                if (whitelist) Environment.Exit(1);
            }
        }

        internal void Apply(PlayerAccessSnapshot update)
        {
            Validate(update, target, DateTimeOffset.UtcNow, requiredRevision);
            if (!Ready) throw new InvalidOperationException("Player access admission hooks are unavailable. Update the Agent and server build.");
            if (snapshot != null && (update.Revision < snapshot.Revision || update.IssuedAt < snapshot.IssuedAt))
                throw new InvalidOperationException("Stale player access policy.");
            var required = update.Enabled && update.Whitelist;
            if (required != whitelist || requiredRevision != update.Revision || !File.Exists(markerPath))
                WriteAtomic(markerPath, JsonConvert.SerializeObject(new AccessMarker { Whitelist = required, Revision = update.Revision }));
            whitelist = required;
            requiredRevision = update.Revision;
            grants = update.Players.ToDictionary(player => player.SteamId);
            snapshot = update;
            lastUpdate = DateTime.MinValue;
            Update(); // Acknowledgement includes promotion revocation before a subsequent save/stop command.
        }

        internal static bool InitialWhitelist(bool? configured, long launchRevision, bool persisted, long persistedRevision) =>
            configured.HasValue && launchRevision >= persistedRevision ? configured.Value : persisted;

        internal static void Validate(PlayerAccessSnapshot update, string expectedTarget, DateTimeOffset now, long minimumRevision = 0)
        {
            if (update == null || update.Version != PlayerAccessSnapshot.ProtocolVersion || update.Target != expectedTarget
                || update.Revision < minimumRevision || update.IssuedAt > now.AddSeconds(30) || update.ExpiresAt <= now
                || update.ExpiresAt > update.IssuedAt.AddMinutes(2) || update.Players == null || update.Players.Count > 10000
                || update.Players.Any(p => p == null || p.SteamId == 0 || p.Promotion < 0 || p.Promotion > 4)
                || update.Players.Select(p => p.SteamId).Distinct().Count() != update.Players.Count)
                throw new InvalidDataException("Invalid or expired player access policy.");
        }

        internal static bool Allows(PlayerAccessSnapshot policy, bool required, ulong steamId, DateTimeOffset now)
        {
            if (!required) return true;
            return policy != null && policy.Enabled && policy.ExpiresAt > now
                && policy.Players.Any(p => p.SteamId == steamId && p.Allowed && p.Confirmed && p.ExpiresAt > now);
        }

        private PlayerAccessGrant Grant(ulong steamId)
        {
            var now = DateTimeOffset.UtcNow;
            return snapshot != null && snapshot.Enabled && snapshot.ExpiresAt > now
                && grants.TryGetValue(steamId, out var player) && player.Allowed && player.Confirmed && player.ExpiresAt > now ? player : null;
        }

        internal void Update()
        {
            if (managed == null || MySession.Static?.Ready != true || DateTime.UtcNow - lastUpdate < TimeSpan.FromSeconds(1)) return;
            lastUpdate = DateTime.UtcNow;
            var session = MySession.Static;
            if (!ReferenceEquals(promotionSource, session))
            {
                if (promotionSource != null) promotionSource.OnUserPromoteLevelChanged -= ManualPromotionChanged;
                promotionSource = session;
                promotionSource.OnUserPromoteLevelChanged += ManualPromotionChanged;
            }
            // Restore offline players too: their elevated role may have been saved before a previous crash.
            foreach (var entry in managed.ToArray())
                ReconcilePromotion(entry.Key, entry.Value, Grant(entry.Key)?.Promotion ?? 0);
            var players = new List<IMyPlayer>();
            Sandbox.ModAPI.MyAPIGateway.Multiplayer?.Players?.GetPlayers(players);
            foreach (var player in players.Where(p => !p.IsBot && p.SteamUserId != 0))
            {
                var grant = Grant(player.SteamUserId);
                if (grant != null && grant.Promotion > 0 && !managed.ContainsKey(player.SteamUserId))
                {
                    var level = session.GetUserPromoteLevel(player.SteamUserId);
                    if (level != MyPromoteLevel.Owner && (int)level < grant.Promotion)
                    {
                        var entry = new ManagedPromotion { Baseline = (int)level };
                        managed.Add(player.SteamUserId, entry);
                        ReconcilePromotion(player.SteamUserId, entry, grant.Promotion);
                    }
                }
                // An outage does not eject existing sessions. Only a fresh, confirmed denial does.
                if (whitelist && snapshot != null && snapshot.ExpiresAt > DateTimeOffset.UtcNow && grant == null)
                {
                    var decision = snapshot.Players.FirstOrDefault(p => p.SteamId == player.SteamUserId);
                    if (decision == null || decision.Confirmed && decision.ExpiresAt > DateTimeOffset.UtcNow)
                        MyMultiplayer.Static?.KickClient(player.SteamUserId, add: false);
                }
            }
        }

        private void ReconcilePromotion(ulong steamId, ManagedPromotion entry, int entitlement)
        {
            var level = MySession.Static.GetUserPromoteLevel(steamId);
            if (level == MyPromoteLevel.Owner) return;
            if ((int)level != entry.Baseline && !entry.Applied.Contains((int)level))
            {
                // An independently changed level becomes the manual baseline; do not erase it on revocation.
                entry.Baseline = (int)level;
                SavePromotions();
            }
            var desired = Math.Max(entry.Baseline, entitlement);
            if ((int)level == desired) return;
            if (desired != entry.Baseline)
            {
                if (!entry.Applied.Contains(desired)) entry.Applied.Add(desired);
                // Journal before granting. Revocation remains possible even if storage has become unwritable.
                try { SavePromotions(); }
                catch { SetPromotion(steamId, entry.Baseline); throw; }
            }
            SetPromotion(steamId, desired);
        }

        private void SetPromotion(ulong steamId, int level)
        {
            applyingPromotion = true;
            try { MySession.Static.SetUserPromoteLevel(steamId, (MyPromoteLevel)level); }
            finally { applyingPromotion = false; }
        }

        private void ManualPromotionChanged(ulong steamId, MyPromoteLevel level)
        {
            if (applyingPromotion || level == MyPromoteLevel.Owner || !managed.TryGetValue(steamId, out var entry) || entry.Baseline == (int)level) return;
            entry.Baseline = (int)level;
            SavePromotions();
        }

        internal void RecordManualPromotion(ulong steamId, MyPromoteLevel level)
        {
            // Ownership is outside this overlay. Never persist Owner as a restorable temporary baseline.
            if (level == MyPromoteLevel.Owner) return;
            if ((Grant(steamId)?.Promotion ?? 0) > (int)level)
                throw new InvalidOperationException("Remove or lower the player's Discord promotion mapping before assigning a lower manual role.");
            if (managed != null && managed.TryGetValue(steamId, out var entry))
            { entry.Baseline = (int)level; SavePromotions(); }
        }

        private void SavePromotions() => WriteAtomic(promotionsPath, JsonConvert.SerializeObject(managed));

        private static void WriteAtomic(string path, string json)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temporary = path + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream))
                { writer.Write(json); writer.Flush(); stream.Flush(true); }
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        public void Dispose()
        {
            harmony?.UnpatchAll(HarmonyId);
            if (promotionSource != null) promotionSource.OnUserPromoteLevelChanged -= ManualPromotionChanged;
            if (ReferenceEquals(current, this)) current = null;
        }

        private static bool IsReserved(List<ulong> reserved, ulong steamId) => reserved.Contains(steamId)
            || current?.Grant(steamId)?.Priority == true;

        [HarmonyPatch]
        private static class AdmissionPatch
        {
            private static readonly MethodInfo Reject = AccessTools.DeclaredMethod(typeof(MyDedicatedServerBase), "UserRejected");
            private static MethodBase TargetMethod() => AccessTools.DeclaredMethod(typeof(MyDedicatedServerBase), "UserAccepted");
            private static bool Prefix(MyDedicatedServerBase __instance, ulong steamId)
            {
                if (current == null || Allows(current.snapshot, current.whitelist, steamId, DateTimeOffset.UtcNow)) return true;
                Reject.Invoke(__instance, new object[] { steamId, JoinResult.NotInGroup });
                return false;
            }
        }

        [HarmonyPatch]
        private static class PriorityPatch
        {
            private static MethodBase TargetMethod() => AccessTools.DeclaredMethod(typeof(MyDedicatedServerBase), "SetUserConnectionResult");
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) => RewritePriority(instructions);
        }

        internal static IEnumerable<CodeInstruction> RewritePriority(IEnumerable<CodeInstruction> instructions)
        {
            var codes = instructions.Select(c => new CodeInstruction(c)).ToList();
            var count = 0;
            for (var i = 2; i < codes.Count; i++)
                if (codes[i].operand is MethodInfo contains && contains.DeclaringType == typeof(List<ulong>) && contains.Name == "Contains"
                    && codes[i - 2].operand is MethodInfo getter && getter.Name == "get_Reserved")
                {
                    codes[i].opcode = OpCodes.Call;
                    codes[i].operand = AccessTools.Method(typeof(PlayerAccessEnforcement), nameof(IsReserved));
                    count++;
                }
            if (count != 1) throw new InvalidOperationException("Expected exactly one vanilla reserved-slot check; admission hooks need updating.");
            return codes;
        }

        private sealed class AccessMarker { public bool Whitelist { get; set; } public long Revision { get; set; } }
        internal sealed class ManagedPromotion
        {
            public int Baseline { get; set; }
            public List<int> Applied { get; set; } = new List<int>();
        }
    }
}
