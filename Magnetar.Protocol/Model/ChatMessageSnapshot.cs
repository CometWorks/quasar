namespace Magnetar.Protocol.Model;

public class ChatMessageSnapshot
{
    public long SteamId { get; set; }

    public string AuthorName { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    public long TimestampTicksUtc { get; set; }
}
