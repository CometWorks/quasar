namespace Quasar.Client.Operations;

public sealed record ChatTarget(string UniqueName, string DisplayName);
public sealed record ChatLine(DateTimeOffset? Time, string Author, string Channel, string Text);
public sealed record ChatSuggestion(string Text, string Syntax, string Description);
public sealed record ChatCommandResult(bool Success, string Message);
public sealed record DashboardChat(ChatTarget[] Targets, int ConnectedCount, string Selected, bool Connected, bool HasSnapshot,
    string? World, int PlayersOnline, int MaxPlayers, bool CanControl, bool CanSend, bool CanSave, bool CanRestart,
    ChatLine[] Messages, ChatSuggestion[] Suggestions, ChatCommandResult[] Results);
