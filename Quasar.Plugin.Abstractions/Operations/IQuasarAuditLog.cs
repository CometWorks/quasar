using System.Security.Claims;
using System.Text.Json;

namespace Quasar.Plugin.Abstractions.Operations;

public enum QuasarAuditPrincipalKind
{
    Person,
    Assistant,
    PlayerAgent,
    Monitor,
}

public enum QuasarAuditMode
{
    Applied,
    Proposed,
    Approved,
    Rejected,
}

public enum QuasarAuditResult
{
    Ok,
    Refused,
    Failed,
}

public sealed record QuasarAuditPrincipal(
    QuasarAuditPrincipalKind Kind,
    string Subject,
    string? Name
);

/// <summary>Who asked for the action. For a person acting directly it is that person.</summary>
/// <param name="Provider">The person's sign-in provider, so an approval can rebuild the asker.</param>
/// <param name="Server">For a player, the server they asked from, so an approval can rebuild the player agent.</param>
public sealed record QuasarAuditRequester(
    string? Subject = null,
    string? SteamId = null,
    string? PlayerName = null,
    string? DiscordUserId = null,
    string? Provider = null,
    string? Server = null
);

/// <summary>One line of the append-only audit log. Entries are never rewritten; a reversal is a new entry.</summary>
public sealed record QuasarAuditEntry
{
    public required string Id { get; init; }
    public required DateTimeOffset Time { get; init; }
    public required QuasarAuditPrincipal Principal { get; init; }
    public required QuasarAuditRequester Requester { get; init; }
    public string? ConversationId { get; init; }
    public string? Request { get; init; }

    /// <summary>The operation key, <c>pluginId/operationId</c>.</summary>
    public required string Operation { get; init; }

    public required string Area { get; init; }
    public string? Server { get; init; }

    /// <summary>The request JSON, with secret-like property values replaced by <c>&lt;redacted&gt;</c>.</summary>
    public JsonElement? Arguments { get; init; }

    public QuasarAuditMode Mode { get; init; }
    public string? ApprovedBy { get; init; }
    public QuasarAuditResult Result { get; init; }
    public string? Error { get; init; }

    /// <summary>Why an agent proposed the call.</summary>
    public string? Reason { get; init; }

    public QuasarReversal Reversal { get; init; }
    public JsonElement? UndoData { get; init; }
    public bool HasBlobs { get; init; }

    /// <summary>The ID of the entry this one reverses.</summary>
    public string? Reverses { get; init; }

    /// <summary>For an approved or rejected call, the ID of the proposal entry.</summary>
    public string? Proposal { get; init; }
}

/// <summary>Text filters match a substring, ignoring case.</summary>
public sealed record QuasarAuditFilter
{
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    public QuasarAuditPrincipalKind? PrincipalKind { get; init; }
    public string? Principal { get; init; }
    public string? Requester { get; init; }
    public string? Operation { get; init; }
    public string? Server { get; init; }
    public QuasarAuditResult? Result { get; init; }
    public int Skip { get; init; }
    public int Take { get; init; } = 100;
}

/// <summary>The audit log of change operations. The dispatcher writes applied and approved entries;
/// there is no undo here, only admins undo from the audit page.</summary>
public interface IQuasarAuditLog
{
    /// <summary>Records an agent's proposal. The dispatch on approval writes its own <c>approved</c> entry.</summary>
    Task<QuasarAuditEntry> RecordProposalAsync(
        ClaimsPrincipal agent,
        string pluginId,
        string operationId,
        JsonElement arguments,
        string? reason,
        CancellationToken token
    );

    /// <summary>Runs a proposed call as the agent that proposed it, rebuilt by core from the proposal entry,
    /// with <paramref name="approver"/> as <see cref="QuasarOperationContext.ApprovedBy"/>. The dispatcher checks
    /// the agent, its asker and the approver again. The resulting <c>approved</c> entry links the proposal.
    /// Refused (403, 404 or 409, with the reason) when the entry is no proposal, has expired, or was already
    /// approved or rejected.</summary>
    Task<QuasarOperationResult> ApproveAsync(
        ClaimsPrincipal approver,
        string proposalEntryId,
        CancellationToken token
    );

    /// <summary>Writes a <c>rejected</c> entry linked to the proposal. The approver must be a person allowed to
    /// run the operation. A null approver means the proposal expired; its reason is then always <c>expired</c>.</summary>
    Task<QuasarOperationResult> RejectAsync(
        ClaimsPrincipal? approver,
        string proposalEntryId,
        string? reason,
        CancellationToken token
    );

    /// <summary>Matching entries, newest first.</summary>
    Task<IReadOnlyList<QuasarAuditEntry>> QueryAsync(
        QuasarAuditFilter filter,
        CancellationToken token
    );
}
