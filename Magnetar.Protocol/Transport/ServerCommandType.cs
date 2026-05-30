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
}
