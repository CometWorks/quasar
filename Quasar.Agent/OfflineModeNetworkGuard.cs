using System;
using System.Net;
using System.Reflection;
using HarmonyLib;
using Sandbox.Engine.Multiplayer;
using Sandbox.Engine.Networking;
using Sandbox.Game.World;
using VRage.Game;
using VRage.GameServices;

namespace Quasar.Agent
{
    internal static class OfflineModeNetworkGuard
    {
        private const string HarmonyId = "quasar.agent.offline-mode-network-guard";
        private static Harmony _harmony;

        public static void Apply()
        {
            if (_harmony != null)
                return;

            try
            {
                _harmony = new Harmony(HarmonyId);
                _harmony.PatchAll(typeof(OfflineModeNetworkGuard).Assembly);
                Console.WriteLine("Quasar offline-mode network guard applied.");
            }
            catch (Exception exception)
            {
                _harmony = null;
                Console.WriteLine($"Quasar offline-mode network guard failed: {exception.Message}");
            }
        }

        public static void Dispose()
        {
            try
            {
                _harmony?.UnpatchAll(HarmonyId);
            }
            catch
            {
            }
            finally
            {
                _harmony = null;
            }
        }

        private static bool AllowClient(ulong steamId)
        {
            if (MySession.Static?.OnlineMode != MyOnlineModeEnum.OFFLINE)
                return true;

            var state = default(MyP2PSessionState);
            var peer = MyGameService.Peer2Peer;
            try
            {
                if (peer != null &&
                    peer.GetSessionState(steamId, ref state) &&
                    !state.UsingRelay &&
                    state.RemoteIP != 0 &&
                    IPAddress.IsLoopback(new IPAddress(state.RemoteIP)))
                {
                    return true;
                }
            }
            catch (Exception exception)
            {
                Console.WriteLine($"Failed checking offline-mode client {steamId}: {exception.Message}");
            }

            peer?.CloseSession(steamId);
            Console.WriteLine($"Rejected non-local client {steamId} because world online mode is Offline.");
            return false;
        }

        [HarmonyPatch]
        private static class OnConnectedClientPatch
        {
            private static MethodBase TargetMethod() =>
                AccessTools.DeclaredMethod(typeof(MyDedicatedServerBase), "OnConnectedClient");

            private static bool Prefix(ulong steamId) => AllowClient(steamId);
        }
    }
}
