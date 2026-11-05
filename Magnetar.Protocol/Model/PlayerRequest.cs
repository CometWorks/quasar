namespace Magnetar.Protocol.Model;

/// <summary>
/// A player's <c>!ask</c> or <c>!link</c> chat command, sent by Quasar.Agent as a
/// <see cref="Transport.WireMessageKind.PlayerRequest"/> message. Quasar takes the
/// server identity from the authenticated connection, never from this payload.
/// </summary>
public class PlayerRequest
{
    public const string AskKind = "ask";
    public const string LinkKind = "link";

    public string RequestId { get; set; } = string.Empty;

    /// <summary><see cref="AskKind"/> or <see cref="LinkKind"/>.</summary>
    public string Kind { get; set; } = string.Empty;

    public long SteamId { get; set; }

    public long IdentityId { get; set; }

    public string PlayerName { get; set; } = string.Empty;

    /// <summary>The question; empty for <see cref="LinkKind"/>.</summary>
    public string Text { get; set; } = string.Empty;
}
