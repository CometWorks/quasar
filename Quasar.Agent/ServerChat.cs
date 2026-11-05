using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Gui;
using VRage.GameServices;
using VRage.Network;

namespace Quasar.Agent
{
    /// <summary>Chat messages the server itself sends: Quasar's broadcasts, whispers and faction messages.</summary>
    internal static class ServerChat
    {
        // MyMultiplayerBase.OnChatMessageReceived_Server runs the per-sender chat anti-spam unless the event
        // sender is the server. A direct SendChatMessage call has no event context, so the sender reads as 0
        // and all of Quasar's messages share one spam budget: a second message within SpamMessagesTime is
        // dropped and the following ones are blocked for SpamMessagesTimeout. Sending inside the context the
        // replication layer gives the server's own locally invoked events makes the sender the server.
        public static void Send(
            MyMultiplayerBase multiplayer,
            string text,
            ChatChannel channel,
            long targetId,
            ChatMessageCustomData? customData = null
        )
        {
            using (AsServer(multiplayer.ServerId))
                multiplayer.SendChatMessage(text, channel, targetId, customData);
        }

        internal static MyEventContext.Token AsServer(ulong serverId) =>
            MyEventContext.Set(new EndpointId(serverId), null, isInvokedLocally: true);
    }
}
