using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Sandbox.Engine.Multiplayer;
using VRage.Network;

namespace Quasar.Agent
{
    internal static class ChatCapturePatch
    {
        private const string HarmonyId = "quasar.agent.chat-capture";
        private static Harmony _harmony;

        public static void Apply()
        {
            if (_harmony != null)
                return;

            try
            {
                _harmony = new Harmony(HarmonyId);
                _harmony.Patch(
                    AccessTools.DeclaredMethod(typeof(MyMultiplayerBase), "OnChatMessageReceived_Server", new[] { typeof(ChatMsg) }),
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(ChatCapturePatch), nameof(Prefix))) { priority = Priority.First },
                    transpiler: new HarmonyMethod(AccessTools.Method(typeof(ChatCapturePatch), nameof(Transpiler))),
                    finalizer: new HarmonyMethod(AccessTools.Method(typeof(ChatCapturePatch), nameof(Finalizer))));
                Console.WriteLine("Quasar accepted-chat capture patch applied.");
            }
            catch (Exception exception)
            {
                Dispose();
                // No unscoped callback fallback: recipient notifications are not new chat messages.
                Console.WriteLine($"Quasar chat capture disabled: receive patch failed: {exception.Message}");
            }
        }

        public static void Dispose()
        {
            try { _harmony?.UnpatchAll(HarmonyId); }
            catch (Exception exception) { Console.WriteLine($"Quasar chat capture cleanup failed: {exception.Message}"); }
            finally { _harmony = null; }
        }

        private static void Prefix(ChatMsg msg, out ChatCaptureScope __state)
        {
            // Keep the authenticated sender before delivery callbacks change the event context.
            // ChatMsg.Author comes from the client and must not override this identity.
            var sender = MyEventContext.Current.Sender.Value;
            var serverId = MyMultiplayer.Static?.ServerId ?? 0UL;
            var isServerMessage = sender == 0 || sender == serverId;
            if (sender == 0)
                sender = serverId;
            __state = new ChatCaptureScope(sender, msg.CustomData?.AuthorName, isServerMessage,
                msg.Text, msg.Channel, msg.TargetId);
        }

        private static void Finalizer(ChatCaptureScope __state) => __state?.Dispose();

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            ChatCaptureTranspiler.MarkAcceptedBeforeDispatch(instructions,
                AccessTools.DeclaredMethod(typeof(MyMultiplayerBase), "OnChatMessage", new[] { typeof(ChatMsg).MakeByRefType() }),
                AccessTools.Method(typeof(ChatCaptureScope), nameof(ChatCaptureScope.MarkAccepted)));
    }

    internal static class ChatCaptureTranspiler
    {
        public static IEnumerable<CodeInstruction> MarkAcceptedBeforeDispatch(
            IEnumerable<CodeInstruction> instructions, MethodInfo dispatch, MethodInfo marker)
        {
            var code = instructions.ToList();
            if (dispatch == null || marker == null || code.Count(instruction => instruction.Calls(dispatch)) != 1)
                throw new InvalidOperationException("Expected exactly one accepted chat dispatch in the server receive handler.");

            var index = code.FindIndex(instruction => instruction.Calls(dispatch));
            // The receive handler has no exception blocks at this call. Fail closed if its shape changes.
            if (code[index].blocks.Count != 0)
                throw new InvalidOperationException("Unexpected exception boundary at accepted chat dispatch.");

            // This call occurs after anti-spam/faction validation and all recipient fan-out.
            // The no-argument marker leaves the original receiver/ref-message stack intact.
            var accepted = new CodeInstruction(OpCodes.Call, marker);
            accepted.labels.AddRange(code[index].labels);
            code[index].labels.Clear();
            code.Insert(index, accepted);
            return code;
        }
    }
}
