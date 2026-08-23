namespace Magnetar.Protocol.Model;

public class ChatMessageSnapshot
{
    public long SteamId { get; set; }

    public string AuthorName { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    public long TimestampTicksUtc { get; set; }

    public bool IsServerMessage { get; set; }

    public ChatMessageChannel Channel { get; set; }

    /// <summary>
    /// Faction id for faction chat, or player identity id for whispers.
    /// </summary>
    public long TargetId { get; set; }

    public string TargetName { get; set; } = string.Empty;

    public string FactionTag { get; set; } = string.Empty;
}

public enum ChatMessageChannel
{
    Unknown = 0,
    Global = 1,
    GlobalScripted = 2,
    Faction = 3,
    Whisper = 4,
    ChatBot = 5,
    BroadcastController = 6,
}
