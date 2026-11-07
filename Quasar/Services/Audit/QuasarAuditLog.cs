using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Magnetar.Protocol.Runtime;
using Microsoft.AspNetCore.Authorization;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Plugin.Abstractions.Security;
using Quasar.Services.Auth;
using Quasar.Services.Plugins;
using QuasarPolicyNames = Quasar.Services.Auth.QuasarPolicyNames;

namespace Quasar.Services.Audit;

public sealed class QuasarAuditOptions
{
    public const int DefaultRetentionDays = 30;

    public string Directory { get; init; } =
        Path.Combine(MagnetarPaths.GetQuasarDirectory(), "audit");

    /// <summary>Entries and undo blobs are kept this long.</summary>
    public int RetentionDays { get; init; } = DefaultRetentionDays;

    public static QuasarAuditOptions Create(IConfiguration configuration) =>
        new()
        {
            RetentionDays =
                int.TryParse(configuration["Quasar:Audit:RetentionDays"], out var days) && days > 0
                    ? days
                    : DefaultRetentionDays,
        };
}

public enum QuasarUndoStatus
{
    Done,
    Conflict,
    Refused,
}

/// <param name="Detail">The difference for a conflict, the reason for a refusal.</param>
public sealed record QuasarUndoOutcome(QuasarUndoStatus Status, string? Detail = null);

/// <summary>
/// Append-only monthly JSONL files in the Quasar data folder (<c>audit/yyyy-MM.jsonl</c>, UTC), with undo blobs
/// in <c>audit/blobs/&lt;entry id&gt;</c>. The operation dispatcher writes an entry for every change operation call.
/// </summary>
public sealed class QuasarAuditLog : BackgroundService, IQuasarAuditLog
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) },
    };

    private readonly QuasarAuditOptions _options;
    private readonly Dictionary<string, QuasarOperation> _operations;
    private readonly RbacConfigCatalog _rbac;
    private readonly QuasarRoleMapper _roles;
    private readonly IAuthorizationService _authorization;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<QuasarAuditLog> _logger;
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly SemaphoreSlim _undo = new(1, 1);

    public QuasarAuditLog(
        QuasarAuditOptions options,
        IEnumerable<QuasarOperation> operations,
        RbacConfigCatalog rbac,
        QuasarRoleMapper roles,
        IAuthorizationService authorization,
        IServiceScopeFactory scopes,
        ILogger<QuasarAuditLog> logger
    )
    {
        _options = options;
        _operations = operations.ToDictionary(operation => operation.Key, StringComparer.Ordinal);
        _rbac = rbac;
        _roles = roles;
        _authorization = authorization;
        _scopes = scopes;
        _logger = logger;
    }

    public int RetentionDays => _options.RetentionDays;

    public async Task<QuasarAuditEntry> RecordProposalAsync(
        ClaimsPrincipal agent,
        string pluginId,
        string operationId,
        JsonElement arguments,
        string? reason,
        CancellationToken token
    )
    {
        if (!agent.IsQuasarAgent(out _))
            throw new ArgumentException("Only an agent principal makes proposals.", nameof(agent));
        if (!_operations.TryGetValue(pluginId + "/" + operationId, out var operation))
            throw new ArgumentException("SDK operation not found.", nameof(operationId));
        if (
            QuasarOperationService.IsDeniedToAgents(
                operation,
                QuasarOperationService.Rule(operation, _rbac.GetConfig())
            )
        )
            throw new UnauthorizedAccessException("Assistants cannot use this operation.");
        var entry = Entry(NewEntryId(), agent, operation, arguments) with
        {
            Mode = QuasarAuditMode.Proposed,
            Reason = reason,
            Reversal = QuasarOperationService.Meta(operation).Reversal,
        };
        await AppendAsync(entry, token);
        return entry;
    }

    public async Task<IReadOnlyList<QuasarAuditEntry>> QueryAsync(
        QuasarAuditFilter filter,
        CancellationToken token
    ) =>
        (await ReadAsync(filter.From, filter.To, token))
            .Where(entry => Matches(entry, filter))
            .OrderByDescending(entry => entry.Time)
            .ThenByDescending(entry => entry.Id, StringComparer.Ordinal)
            .Skip(Math.Max(0, filter.Skip))
            .Take(Math.Clamp(filter.Take, 1, 1000))
            .ToList();

    /// <summary>Reverses an entry. The caller needs security administration and the original operation's policies.</summary>
    public async Task<QuasarUndoOutcome> UndoAsync(
        ClaimsPrincipal caller,
        string entryId,
        bool force,
        CancellationToken token
    )
    {
        caller = _roles.RefreshRoles(caller);
        if (
            !await QuasarAgentPrincipals.IsPersonAsync(
                caller,
                _authorization,
                QuasarPolicyNames.CanManageSecurity
            )
        )
            return new(QuasarUndoStatus.Refused, "Undo needs security administration permission.");

        await _undo.WaitAsync(token);
        try
        {
            var entries = await ReadAsync(null, null, token);
            var entry = entries.FirstOrDefault(entry => entry.Id == entryId);
            if (entry is null)
                return new(QuasarUndoStatus.Refused, "Audit entry not found.");
            if (UndoBlocker(entry, entries) is { } blocker)
                return new(QuasarUndoStatus.Refused, blocker);
            if (
                !_operations.TryGetValue(entry.Operation, out var operation)
                || QuasarOperationService.Meta(operation).Undo is not { } handler
            )
                return new(
                    QuasarUndoStatus.Refused,
                    "The operation is no longer registered with an undo handler."
                );

            await using var scope = _scopes.CreateAsyncScope();
            var denied = await scope
                .ServiceProvider.GetRequiredService<QuasarOperationService>()
                .CheckAccessAsync(
                    caller,
                    operation.PluginId,
                    operation.OperationId,
                    operation.Kind
                );
            if (denied is not null)
                return new(QuasarUndoStatus.Refused, denied.Error);

            var context = new QuasarUndoContext(
                scope.ServiceProvider,
                caller,
                entry.UndoData ?? default,
                BlobDirectory(entry.Id)
            );
            var difference = await handler.CheckAsync(context, token);
            if (difference is not null && !force)
                return new(QuasarUndoStatus.Conflict, difference);

            var reversal = Entry(
                NewEntryId(),
                caller,
                operation,
                JsonSerializer.SerializeToElement(new { force }, Json)
            ) with
            {
                Server = entry.Server,
                Reverses = entry.Id,
            };
            try
            {
                await handler.ReverseAsync(context, token);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                await AppendAsync(
                    reversal with
                    {
                        Time = DateTimeOffset.UtcNow,
                        Result = QuasarAuditResult.Failed,
                        Error = error.Message,
                    },
                    CancellationToken.None
                );
                return new(QuasarUndoStatus.Refused, "Undo failed: " + error.Message);
            }
            await AppendAsync(
                reversal with
                {
                    Time = DateTimeOffset.UtcNow,
                },
                CancellationToken.None
            );
            return new(QuasarUndoStatus.Done);
        }
        finally
        {
            _undo.Release();
        }
    }

    /// <summary>Why an entry cannot be undone, or null when it can.</summary>
    public string? UndoBlocker(QuasarAuditEntry entry, IEnumerable<QuasarAuditEntry> entries)
    {
        if (entry.Result != QuasarAuditResult.Ok || entry.Mode == QuasarAuditMode.Proposed)
            return "Only a change that was carried out can be undone.";
        if (entry.Reversal != QuasarReversal.Undo)
            return "This change is not reversible.";
        if (
            entry.Time < DateTimeOffset.UtcNow.AddDays(-_options.RetentionDays)
            || (entry.HasBlobs && !Directory.Exists(BlobDirectory(entry.Id)))
        )
            return "The undo data of this change has expired.";
        if (
            entries.Any(other => other.Reverses == entry.Id && other.Result == QuasarAuditResult.Ok)
        )
            return "This change has already been undone.";
        return null;
    }

    internal string NewEntryId() =>
        DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture)
        + "-"
        + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));

    internal string BlobDirectory(string entryId) =>
        Path.Combine(_options.Directory, "blobs", entryId);

    /// <summary>Writes a dispatched change operation call. Never throws: the call has already happened.</summary>
    internal async Task RecordCallAsync(
        string entryId,
        ClaimsPrincipal user,
        ClaimsPrincipal? approver,
        QuasarOperation operation,
        JsonElement request,
        QuasarUndoRecorder? undo,
        QuasarAuditResult result,
        string? error
    )
    {
        try
        {
            var reversal = QuasarOperationService.Meta(operation).Reversal;
            if (
                result != QuasarAuditResult.Ok
                || (reversal == QuasarReversal.Undo && undo?.Recorded != true)
            )
                reversal = QuasarReversal.NotReversible;
            if (reversal != QuasarReversal.Undo && Directory.Exists(BlobDirectory(entryId)))
                Directory.Delete(BlobDirectory(entryId), recursive: true);
            await AppendAsync(
                Entry(entryId, user, operation, request) with
                {
                    Mode = approver is null ? QuasarAuditMode.Applied : QuasarAuditMode.Approved,
                    ApprovedBy = approver?.FindFirstValue(ClaimTypes.NameIdentifier),
                    Result = result,
                    Error = error,
                    Server = undo?.Server,
                    Reversal = reversal,
                    UndoData = reversal == QuasarReversal.Undo ? undo?.Data : null,
                    HasBlobs = reversal == QuasarReversal.Undo && undo?.HasBlobs == true,
                },
                CancellationToken.None
            );
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogError(
                exception,
                "Could not write the audit entry of {Operation} ({Result}).",
                operation.Key,
                result
            );
        }
    }

    internal static JsonElement Redact(JsonElement value)
    {
        var node = JsonNode.Parse(value.GetRawText());
        Redact(node);
        return JsonSerializer.SerializeToElement(node);
    }

    // Retention: blob folders of old entries go first, a month file once all of its entries are past retention.
    internal void Prune(DateTimeOffset now)
    {
        var cutoff = now.AddDays(-_options.RetentionDays);
        var blobs = Path.Combine(_options.Directory, "blobs");
        if (Directory.Exists(blobs))
            foreach (var folder in Directory.EnumerateDirectories(blobs))
                if (IdTime(Path.GetFileName(folder)) is { } time && time < cutoff)
                    Directory.Delete(folder, recursive: true);
        if (Directory.Exists(_options.Directory))
            foreach (var file in Directory.EnumerateFiles(_options.Directory, "*.jsonl"))
                if (MonthOf(file) is { } month && month.AddMonths(1) <= cutoff)
                    File.Delete(file);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromDays(1));
        do
        {
            try
            {
                Prune(DateTimeOffset.UtcNow);
            }
            catch (Exception exception)
                when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(exception, "Could not prune the audit log.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private QuasarAuditEntry Entry(
        string id,
        ClaimsPrincipal user,
        QuasarOperation operation,
        JsonElement? arguments
    )
    {
        user.IsQuasarAgent(out var agent);
        return new()
        {
            Id = id,
            Time = DateTimeOffset.UtcNow,
            Principal = agent is null
                ? new(QuasarAuditPrincipalKind.Person, Subject(user), user.GetQuasarDisplayName())
                : new(
                    agent.Kind == QuasarAgentKind.Assistant
                        ? QuasarAuditPrincipalKind.Assistant
                        : QuasarAuditPrincipalKind.PlayerAgent,
                    agent.Subject,
                    agent.Name
                ),
            Requester =
                agent is null ? Person(user)
                : agent.Asker is { } asker ? Person(asker)
                : new(
                    SteamId: agent.PlayerSteamId,
                    PlayerName: agent.PlayerName,
                    DiscordUserId: agent.DiscordUserId
                ),
            ConversationId = agent?.ConversationId,
            Request = agent?.Request,
            Operation = operation.Key,
            Area = QuasarOperationService.Meta(operation).Area!,
            Arguments = arguments is { ValueKind: not JsonValueKind.Undefined } value
                ? Redact(value)
                : null,
        };
    }

    private static QuasarAuditRequester Person(ClaimsPrincipal user) =>
        new(Subject(user), user.FindFirstValue(QuasarClaimTypes.SteamId));

    private static string Subject(ClaimsPrincipal user) =>
        user.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown";

    private async Task AppendAsync(QuasarAuditEntry entry, CancellationToken token)
    {
        var line = JsonSerializer.Serialize(entry, Json) + "\n";
        await _write.WaitAsync(token);
        try
        {
            Directory.CreateDirectory(_options.Directory);
            var file = Path.Combine(
                _options.Directory,
                entry.Time.UtcDateTime.ToString("yyyy-MM", CultureInfo.InvariantCulture) + ".jsonl"
            );
            await File.AppendAllTextAsync(file, line, CancellationToken.None);
        }
        finally
        {
            _write.Release();
        }
    }

    private async Task<List<QuasarAuditEntry>> ReadAsync(
        DateTimeOffset? from,
        DateTimeOffset? to,
        CancellationToken token
    )
    {
        var entries = new List<QuasarAuditEntry>();
        if (!Directory.Exists(_options.Directory))
            return entries;
        foreach (var file in Directory.EnumerateFiles(_options.Directory, "*.jsonl"))
        {
            if (
                MonthOf(file) is not { } month
                || (from is not null && month.AddMonths(1) <= from)
                || (to is not null && month > to)
            )
                continue;
            foreach (var line in await File.ReadAllLinesAsync(file, token))
            {
                try
                {
                    if (
                        JsonSerializer.Deserialize<QuasarAuditEntry>(line, Json) is { } entry
                        && (from is null || entry.Time >= from)
                        && (to is null || entry.Time <= to)
                    )
                        entries.Add(entry);
                }
                catch (JsonException) { } // A torn or hand-edited line hides only itself.
            }
        }
        return entries;
    }

    private static bool Matches(QuasarAuditEntry entry, QuasarAuditFilter filter) =>
        (filter.PrincipalKind is null || entry.Principal.Kind == filter.PrincipalKind)
        && (filter.Result is null || entry.Result == filter.Result)
        && Contains(filter.Principal, entry.Principal.Subject, entry.Principal.Name)
        && Contains(
            filter.Requester,
            entry.Requester.Subject,
            entry.Requester.SteamId,
            entry.Requester.PlayerName,
            entry.Requester.DiscordUserId
        )
        && Contains(filter.Operation, entry.Operation)
        && Contains(filter.Server, entry.Server);

    private static bool Contains(string? filter, params string?[] values) =>
        string.IsNullOrWhiteSpace(filter)
        || values.Any(value =>
            value?.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase) == true
        );

    private static DateTimeOffset? MonthOf(string file) =>
        DateTimeOffset.TryParseExact(
            Path.GetFileNameWithoutExtension(file),
            "yyyy-MM",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var month
        )
            ? month
            : null;

    private static DateTimeOffset? IdTime(string id) =>
        id.Length >= 17
        && DateTimeOffset.TryParseExact(
            id[..17],
            "yyyyMMddHHmmssfff",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var time
        )
            ? time
            : null;

    private static void Redact(JsonNode? node)
    {
        if (node is JsonObject properties)
        {
            foreach (var (name, child) in properties.ToList())
                if (IsSecret(name))
                    properties[name] = "<redacted>";
                else
                    Redact(child);
        }
        else if (node is JsonArray items)
            foreach (var item in items)
                Redact(item);
    }

    private static bool IsSecret(string name)
    {
        var lower = name.ToLowerInvariant().Replace("_", "").Replace("-", "");
        return lower.Contains("password")
            || lower.Contains("token")
            || lower.Contains("secret")
            || lower.EndsWith("key", StringComparison.Ordinal)
            || lower.EndsWith("keys", StringComparison.Ordinal);
    }
}
