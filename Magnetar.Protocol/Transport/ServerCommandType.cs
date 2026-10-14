namespace Magnetar.Protocol.Transport;

public enum ServerCommandType
{
    Unknown = 0,
    Refresh = 1,
    SendChat = 2,
    SaveWorld = 3,
    StopServer = 4,
    KickPlayer = 5,
    BanPlayer = 6,
    UnbanPlayer = 7,
    PromotePlayer = 8,
    DemotePlayer = 9,
    ListEntities = 10,
    DeleteEntity = 11,
    SetPlayerPromoteLevel = 12,
    ClearKickCooldown = 13,
    SetProfilerMode = 14,
    PluginRequest = 15,
    SendWhisper = 16,
    SendFactionChat = 17,

    /// <summary>
    /// Runs Text, a chat command with its "!", through the server's registered
    /// commands as the console. Replies come back as Console chat lines.
    /// </summary>
    RunChatCommand = 18,
}
