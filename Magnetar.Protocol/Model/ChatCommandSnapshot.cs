using System.Collections.Generic;

namespace Magnetar.Protocol.Model;

public class ChatCommandSnapshot
{
    public string Text { get; set; } = string.Empty;

    public string Syntax { get; set; } = string.Empty;

    public string Prefix { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string HelpText { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string OwnerId { get; set; } = string.Empty;

    public string MinimumPromoteLevel { get; set; } = string.Empty;

    public List<string> PathSegments { get; set; } = new List<string>();
}
