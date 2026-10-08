using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Magnetar.Protocol.Model;
using Microsoft.Extensions.Logging.Abstractions;
using Quasar.Agent;
using Quasar.Services.Discord;
using Xunit;

namespace Quasar.Tests;

public sealed class AgentChatCaptureTests
{
    private const byte Global = 0;
    private const byte Private = 3;
    [Fact]
    public void HarmonyHookCapturesOnlyAcceptedDispatchAndClearsStateOnReturnOrException()
    {
        const string harmonyId = "quasar.tests.accepted-chat";
        var harmony = new Harmony(harmonyId);
        var fixture = typeof(AgentChatCaptureTests);
        var receive = AccessTools.Method(fixture, nameof(Receive));
        try
        {
            harmony.Patch(receive,
                prefix: new HarmonyMethod(AccessTools.Method(fixture, nameof(ReceivePrefix))),
                transpiler: new HarmonyMethod(AccessTools.Method(fixture, nameof(ReceiveTranspiler))),
                finalizer: new HarmonyMethod(AccessTools.Method(fixture, nameof(ReceiveFinalizer))));

            Assert.Equal(0, receive.Invoke(null, [false, 100, false]));
            Assert.False(ChatCaptureScope.TryCapture("tested", Global, 0, out _));
            // Independent sends of identical text must each relay once, regardless of fan-out size.
            Assert.Equal(1, receive.Invoke(null, [true, 100, false]));
            Assert.Equal(1, receive.Invoke(null, [true, 0, false]));
            Assert.False(ChatCaptureScope.TryCapture("tested", Global, 0, out _));
            var exception = Assert.Throws<TargetInvocationException>(() => receive.Invoke(null, [true, 100, true]));
            Assert.IsType<InvalidOperationException>(exception.InnerException);
            Assert.False(ChatCaptureScope.TryCapture("tested", Global, 0, out _));
        }
        finally { harmony.UnpatchAll(harmonyId); }
    }

    [Fact]
    public void UnexpectedServerMethodShapeFailsClosed()
    {
        var dispatch = AccessTools.Method(typeof(AgentChatCaptureTests), nameof(Dispatch));
        var marker = AccessTools.Method(typeof(ChatCaptureScope), nameof(ChatCaptureScope.MarkAccepted));
        Assert.Throws<InvalidOperationException>(() => ChatCaptureTranspiler.MarkAcceptedBeforeDispatch(
            [new CodeInstruction(OpCodes.Ret)], dispatch, marker));
        Assert.Throws<InvalidOperationException>(() => ChatCaptureTranspiler.MarkAcceptedBeforeDispatch(
            [new CodeInstruction(OpCodes.Call, dispatch), new CodeInstruction(OpCodes.Call, dispatch)], dispatch, marker));
    }

    [Fact]
    public void RecipientFanOutCapturesOnceWithTheOriginalSender()
    {
        using var receive = new ChatCaptureScope(123, null!, false, "tested", Global, 0);
        // Per-recipient notifications occur before the server accepts its own history entry.
        for (var recipient = 0; recipient < 100; recipient++)
            Assert.False(ChatCaptureScope.TryCapture("tested", Global, 0, out _));
        ChatCaptureScope.MarkAccepted();
        Assert.True(ChatCaptureScope.TryCapture("tested", Global, 0, out var source));
        Assert.Equal(123UL, source.Sender);
        Assert.False(source.IsServerMessage);
        for (var recipient = 0; recipient < 100; recipient++)
            Assert.False(ChatCaptureScope.TryCapture("tested", Global, 0, out _));
    }

    [Fact]
    public void RepeatedPlayerTextInSeparateReceiveOperationsIsPreserved()
    {
        for (var send = 0; send < 2; send++)
        {
            using var receive = new ChatCaptureScope(123, null!, false, "tested", Global, 0);
            ChatCaptureScope.MarkAccepted();
            Assert.True(ChatCaptureScope.TryCapture("tested", Global, 0, out _));
        }
    }

    [Fact]
    public void QuasarAndOtherLocalSendsKeepTheirServerIdentity()
    {
        using var receive = new ChatCaptureScope(456, "Discord [SPRT]", true, "announcement", Private, 789);
        ChatCaptureScope.MarkAccepted();
        Assert.True(ChatCaptureScope.TryCapture("announcement", Private, 789, out var source));
        Assert.True(source.IsServerMessage);
        Assert.Equal(456UL, source.Sender);
        Assert.Equal("Discord [SPRT]", source.AuthorName);
        Assert.False(ChatCaptureScope.TryCapture("announcement", Private, 789, out _));
    }

    [Fact]
    public void AcceptedQuasarBroadcastTravelsThroughSnapshotAndRelaysOnce()
    {
        var relay = new DiscordChatRelayService(null!, null!, NullLogger<DiscordChatRelayService>.Instance);
        var settings = new DiscordServerOptions { UniqueName = "survival", ChatRelayChannelId = 10 };
        Assert.Empty(relay.CollectFreshMessages(settings, []));

        using var send = new ChatCaptureScope(456, "Server", true, "announcement", Global, 0, isBroadcastSend: true);
        Assert.False(ChatCaptureScope.TryCapture("announcement", Global, 0, out _));
        using var receive = new ChatCaptureScope(123, null!, false, "announcement", Global, 0);
        Assert.False(ChatCaptureScope.TryCapture("announcement", Global, 0, out _));
        ChatCaptureScope.MarkAccepted();
        Assert.True(ChatCaptureScope.TryCapture("announcement", Global, 0, out var source));
        Assert.True(source.IsQuasarBroadcast);
        Assert.True(source.IsServerMessage);
        Assert.Equal(456UL, source.Sender);
        Assert.False(ChatCaptureScope.TryCapture("announcement", Global, 0, out _));

        var snapshot = new ChatMessageSnapshot
        {
            SteamId = (long)source.Sender, AuthorName = "Server", Content = "announcement",
            IsServerMessage = source.IsServerMessage, IsQuasarBroadcast = source.IsQuasarBroadcast,
            Channel = ChatMessageChannel.Global, TimestampTicksUtc = DateTimeOffset.UtcNow.AddSeconds(1).UtcTicks,
        };
        // The additive origin flag must survive the wire payload.
        snapshot = System.Text.Json.JsonSerializer.Deserialize<ChatMessageSnapshot>(
            System.Text.Json.JsonSerializer.Serialize(snapshot))!;
        Assert.Equal("**Server**: announcement", Assert.Single(relay.CollectFreshMessages(settings, [snapshot])).Content);
        Assert.Empty(relay.CollectFreshMessages(settings, [snapshot]));
    }

    [Fact]
    public void QuasarBroadcastOriginDoesNotLeakToDifferentOrNestedReceives()
    {
        using (var send = new ChatCaptureScope(456, "Server", true, "announcement", Global, 0, isBroadcastSend: true))
        {
            using (var unrelated = new ChatCaptureScope(123, null!, false, "other", Global, 0))
                Assert.False(unrelated.IsQuasarBroadcast);
            using (var privateChat = new ChatCaptureScope(123, null!, false, "announcement", Private, 0))
                Assert.False(privateChat.IsQuasarBroadcast);
            using (var targeted = new ChatCaptureScope(123, null!, false, "announcement", Global, 789))
                Assert.False(targeted.IsQuasarBroadcast);
            using (var receive = new ChatCaptureScope(0, null!, true, "announcement", Global, 0))
            {
                Assert.True(receive.IsQuasarBroadcast);
                using var nested = new ChatCaptureScope(123, null!, false, "announcement", Global, 0);
                Assert.False(nested.IsQuasarBroadcast);
            }
        }
        using var later = new ChatCaptureScope(456, "Server", true, "announcement", Global, 0);
        Assert.False(later.IsQuasarBroadcast);
    }

    [Fact]
    public void UnrelatedAndPrivateCallbacksCannotConsumeTheGlobalReceive()
    {
        using var receive = new ChatCaptureScope(123, null!, false, "tested", Global, 0);
        ChatCaptureScope.MarkAccepted();
        Assert.False(ChatCaptureScope.TryCapture("other text", Global, 0, out _));
        Assert.False(ChatCaptureScope.TryCapture("tested", Private, 0, out _));
        Assert.False(ChatCaptureScope.TryCapture("tested", Global, 789, out _));
        Assert.True(ChatCaptureScope.TryCapture("tested", Global, 0, out _));
    }

    [Fact]
    public void NestedSendsRestoreTheOuterReceiveAndDoNotStealItsIdentity()
    {
        using var outer = new ChatCaptureScope(123, null!, false, "outer", Global, 0);
        using (var inner = new ChatCaptureScope(456, "Server", true, "inner", Private, 789))
        {
            ChatCaptureScope.MarkAccepted();
            Assert.False(ChatCaptureScope.TryCapture("outer", Global, 0, out _));
            Assert.True(ChatCaptureScope.TryCapture("inner", Private, 789, out var source));
            Assert.Equal(456UL, source.Sender);
        }
        ChatCaptureScope.MarkAccepted();
        Assert.True(ChatCaptureScope.TryCapture("outer", Global, 0, out var restored));
        Assert.Equal(123UL, restored.Sender);
    }

    [Fact]
    public void CleanupIsIdempotentAndReceiveScopesDoNotCrossThreads()
    {
        using var outer = new ChatCaptureScope(123, null!, false, "tested", Global, 0);
        var inner = new ChatCaptureScope(456, null!, true, "inner", Global, 0);
        inner.Dispose();
        inner.Dispose();
        ChatCaptureScope.MarkAccepted();

        var capturedOnOtherThread = true;
        var thread = new Thread(() => capturedOnOtherThread = ChatCaptureScope.TryCapture("tested", Global, 0, out _));
        thread.Start();
        thread.Join();
        Assert.False(capturedOnOtherThread);
        Assert.True(ChatCaptureScope.TryCapture("tested", Global, 0, out var source));
        Assert.Equal(123UL, source.Sender);
    }

    [Fact]
    public void UnscopedAndAbortedReceivesLeaveNoCaptureState()
    {
        Assert.False(ChatCaptureScope.TryCapture("tested", Global, 0, out _));
        Assert.Throws<InvalidOperationException>((Action)(() =>
        {
            using var receive = new ChatCaptureScope(123, null!, false, "tested", Global, 0);
            throw new InvalidOperationException();
        }));
        Assert.False(ChatCaptureScope.TryCapture("tested", Global, 0, out _));
        // A rejected receive never raises a matching callback.
        using (new ChatCaptureScope(123, null!, false, "tested", Global, 0))
            Assert.False(ChatCaptureScope.TryCapture("tested", Global, 0, out _));
        Assert.False(ChatCaptureScope.TryCapture("tested", Global, 0, out _));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Receive(bool accepted, int recipients, bool fail)
    {
        var count = 0;
        for (var recipient = 0; recipient < recipients; recipient++)
            if (ChatCaptureScope.TryCapture("tested", Global, 0, out _)) count++;
        if (!accepted) return count;
        if (Dispatch()) count++;
        if (fail) throw new InvalidOperationException();
        return count;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool Dispatch() => ChatCaptureScope.TryCapture("tested", Global, 0, out var source) && source.Sender == 123;

    private static void ReceivePrefix(out ChatCaptureScope __state) =>
        __state = new ChatCaptureScope(123, null!, false, "tested", Global, 0);

    private static void ReceiveFinalizer(ChatCaptureScope __state) => __state?.Dispose();

    private static IEnumerable<CodeInstruction> ReceiveTranspiler(IEnumerable<CodeInstruction> code) =>
        ChatCaptureTranspiler.MarkAcceptedBeforeDispatch(code,
            AccessTools.Method(typeof(AgentChatCaptureTests), nameof(Dispatch)),
            AccessTools.Method(typeof(ChatCaptureScope), nameof(ChatCaptureScope.MarkAccepted)));
}
