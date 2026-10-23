namespace Magnetar.Protocol.Transport;

public static class AgentMessageLimits
{
    public const int HelloBytes = 64 * 1024;

    // Companion replies can contain voxel data expanded by base64 and nested JSON envelopes.
    // This limit applies only after Hello authentication; it is measured on the UTF-8 wire message.
    public const int MessageBytes = 64 * 1024 * 1024;
}
