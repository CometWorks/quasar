namespace Magnetar.Protocol.Transport;

public static class WireMessageKind
{
    public const string Hello = "hello";
    public const string Snapshot = "snapshot";
    public const string Command = "command";
    public const string CommandResult = "command-result";
    public const string Ping = "ping";
    public const string Pong = "pong";
    public const string PluginConfigSnapshot = "plugin-config-snapshot";
    public const string PluginConfigUpdate = "plugin-config-update";
    public const string AdminStop = "admin-stop";
    public const string AdminRestart = "admin-restart";
    public const string PluginLogs = "plugin-logs";
}
