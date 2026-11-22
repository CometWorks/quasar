namespace Quasar.Client.Administration;

public sealed record AuditQuery(DateTimeOffset? From, DateTimeOffset? To, string PrincipalKind, string Principal,
    string Requester, string Operation, string Server, string Result, int Take);
public sealed record AuditRow(string Id, DateTimeOffset Time, string Principal, string Requester, string Operation,
    string? Server, string Mode, string Result, string? ConversationId, string? Request, string? Reason,
    string Arguments, string? Error, string Reversal, string? UndoData, bool HasBlobs, string? Reverses,
    bool Undone, string? Blocker);
public sealed record AuditPage(int RetentionDays, AuditRow[] Entries);
public sealed record AuditUndo(string EntryId, bool Force);
public sealed record AuditOutcome(string Status, string? Detail);
