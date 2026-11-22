using System.Text.Json;
using Quasar.Client.Administration;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Services.Audit;
using Quasar.Services.Auth;

namespace Quasar.Services.Plugins;

internal static class QuasarAuditDashboardOperations
{
    private static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };

    internal static void AddQuasarAuditDashboard(this IServiceCollection services)
    {
        const string plugin = QuasarCoreOperations.PluginId;
        string[] policies = [QuasarPolicyNames.CanManageSecurity];
        var metadata = new QuasarOperationMetadata { AgentAccess = QuasarAgentAccess.Denied, RequireDefaultPolicies = true };
        services.AddQuasarOperation<AuditQuery, AuditPage>(plugin, "audit.query", policies,
            async (caller, input, token) =>
            {
                var audit = caller.Services.GetRequiredService<QuasarAuditLog>();
                var entries = await audit.QueryAsync(new()
                {
                    From = input.From, To = input.To, Principal = input.Principal, Requester = input.Requester,
                    Operation = input.Operation, Server = input.Server, Take = input.Take,
                    PrincipalKind = Enum.TryParse<QuasarAuditPrincipalKind>(input.PrincipalKind, out var kind) ? kind : null,
                    Result = Enum.TryParse<QuasarAuditResult>(input.Result, out var result) ? result : null,
                }, token);
                var reversed = entries.Where(entry => entry.Result == QuasarAuditResult.Ok && entry.Reverses is not null)
                    .Select(entry => entry.Reverses).ToHashSet();
                return new(audit.RetentionDays, entries.Select(entry => new AuditRow(entry.Id, entry.Time,
                    $"{entry.Principal.Kind}: {entry.Principal.Name ?? entry.Principal.Subject}",
                    FormatRequester(entry.Requester), entry.Operation, entry.Server,
                    entry.Mode + (entry.ApprovedBy is null ? null : " by " + entry.ApprovedBy), entry.Result.ToString(),
                    entry.ConversationId, entry.Request, entry.Reason, Pretty(entry.Arguments) ?? "none", entry.Error,
                    entry.Reversal.ToString(), Pretty(entry.UndoData), entry.HasBlobs, entry.Reverses,
                    reversed.Contains(entry.Id), audit.UndoBlocker(entry, entries))).ToArray());
            }, metadata with { Description = "Returns a filtered page of audit history.", Effect = QuasarOperationEffect.Read });
        services.AddQuasarOperation<AuditUndo, AuditOutcome>(plugin, "audit.undo", policies,
            async (caller, input, token) =>
            {
                var outcome = await caller.Services.GetRequiredService<QuasarAuditLog>().UndoAsync(caller.User, input.EntryId, input.Force, token);
                return new(outcome.Status.ToString(), outcome.Detail);
            }, metadata with { Description = "Reverses an eligible recorded operation after checking its current state." });
    }

    private static string? Pretty(JsonElement? value) => value is { } json
        ? JsonSerializer.Serialize(QuasarAuditLog.Redact(json), PrettyJson) : null;

    private static string FormatRequester(QuasarAuditRequester requester) => string.Join(" ", new[]
    {
        requester.Subject, requester.PlayerName, requester.SteamId is null ? null : "Steam " + requester.SteamId,
        requester.DiscordUserId is null ? null : "Discord " + requester.DiscordUserId,
    }.Where(part => !string.IsNullOrWhiteSpace(part)).Distinct());
}
