namespace Magnetar.Protocol.Model;

/// <summary>
/// A player's authorization or a UI plugin's chat command (see <see cref="ChatCommandInfo"/>), sent by Quasar.Agent as a
/// <see cref="Transport.WireMessageKind.PlayerRequest"/> message. Quasar takes the
/// server identity from the authenticated connection, never from this payload.
/// </summary>
public class PlayerRequest
{
    public const string AuthorizeKind = "authorize";

    public string RequestId { get; set; } = string.Empty;

    /// <summary><see cref="AuthorizeKind"/> or the plugin command's name.</summary>
    public string Kind { get; set; } = string.Empty;

    public long SteamId { get; set; }

    public long IdentityId { get; set; }

    public string PlayerName { get; set; } = string.Empty;

    /// <summary>The command's text; empty for <see cref="AuthorizeKind"/>.</summary>
    public string Text { get; set; } = string.Empty;
}
