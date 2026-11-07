namespace Magnetar.Protocol.Model;

/// <summary>
/// A UI plugin's player command that Quasar.Agent registers as <c>!Name &lt;Argument&gt;</c>. Quasar sends the
/// list as a <see cref="Transport.WireMessageKind.ChatCommands"/> message once the agent's hello is accepted;
/// a use comes back as a <see cref="PlayerRequest"/> with the name as its kind.
/// </summary>
public class ChatCommandInfo
{
    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Argument { get; set; } = string.Empty;

    /// <summary>The reply right after the player sends the command; the answer comes later as whispers.</summary>
    public string Acknowledgement { get; set; } = string.Empty;
}
