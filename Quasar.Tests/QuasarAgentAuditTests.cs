using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Bunit;
using Magnetar.Protocol.Runtime;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;
using Quasar.Components.Pages;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Plugin.Abstractions.Security;
using Quasar.Services.Audit;
using Quasar.Services.Auth;
using Quasar.Services.Plugins;
using Xunit;
using QuasarPolicyNames = Quasar.Services.Auth.QuasarPolicyNames;

namespace Quasar.Tests;

[Collection("Exact server creation")]
public sealed class QuasarAgentAuditTests : IDisposable
{
    private const string Plugin = "test.plugin";
    private const string AgentDenied = "Assistants cannot use this operation.";

    private static readonly string[] DenyPatternOperations =
    [
        "security.settings.read",
        "rbac.read",
        "auth.settings.read",
        "service-principals.list",
        "update.check",
        "updates.apply",
        "workshop.credentials.read",
        "discord.token.read",
        "assistant.settings.read",
        "llm.keys.read",
    ];

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "quasar-audit-" + Guid.NewGuid().ToString("N")
    );
    private readonly FieldInfo _cache = typeof(MagnetarPaths).GetField(
        "_cachedQuasarDirectory",
        BindingFlags.NonPublic | BindingFlags.Static
    )!;
    private readonly object? _previous;
    private readonly RbacConfigCatalog _rbac;
    private readonly ServiceProvider _services;
    private readonly ServiceDescriptor[] _descriptors;
    private int _value;
    private string? _restoredBlob;

    public QuasarAgentAuditTests()
    {
        _previous = _cache.GetValue(null);
        _cache.SetValue(null, _root);
        Directory.CreateDirectory(_root);
        _rbac = new(NullLogger<RbacConfigCatalog>.Instance);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_rbac);
        services.AddSingleton(new QuasarAuthOptions());
        services.AddSingleton<QuasarRoleMapper>();
        services.AddAuthorization(Program.ConfigureAuthorizationPolicies);
        services.AddSingleton<IAuthorizationPolicyProvider, QuasarAuthorizationPolicyProvider>();
        services.AddScoped<QuasarOperationService>();
        services.AddSingleton(new QuasarAuditOptions { Directory = Path.Combine(_root, "audit") });
        services.AddSingleton<QuasarAuditLog>();
        services.AddSingleton<QuasarAgentPrincipals>();
        var read = new QuasarOperationMetadata { Effect = QuasarOperationEffect.Read };
        Add(services, "servers.read", [QuasarPolicyNames.CanView], read);
        Add(
            services,
            "servers.secret",
            [QuasarPolicyNames.CanView],
            read with
            {
                AgentAccess = QuasarAgentAccess.Denied,
            }
        );
        Add(services, "admin.read", [QuasarPolicyNames.CanManageSecurity], read);
        Add(services, "power.read", [QuasarPolicyNames.CanShutdownQuasar], read);
        Add(services, "my.grids", [QuasarPolicyNames.CanActAsPlayer], read);
        foreach (var operation in DenyPatternOperations)
            Add(services, operation, [QuasarPolicyNames.CanView], read);
        services.AddQuasarOperation<SetRequest, object?>(
            Plugin,
            "servers.set",
            [QuasarPolicyNames.CanEditServers],
            (context, input, token) =>
            {
                if (!input.Skip)
                    context.Undo!.SetData(new { before = _value, after = input.Value });
                context.Undo!.Server = "alpha";
                _value = input.Value;
                return Task.FromResult<object?>(null);
            },
            new() { Reversal = QuasarReversal.Undo, Undo = new ValueUndo(this) }
        );
        services.AddQuasarOperation<JsonElement, object?>(
            Plugin,
            "files.set",
            [QuasarPolicyNames.CanEditServers],
            async (context, input, token) =>
            {
                await context.Undo!.AddBlobAsync(
                    "old.txt",
                    new MemoryStream("old content"u8.ToArray()),
                    token
                );
                return null;
            },
            new() { Reversal = QuasarReversal.Undo, Undo = new BlobUndo(this) }
        );
        services.AddQuasarOperation<JsonElement, object?>(
            Plugin,
            "servers.fail",
            [QuasarPolicyNames.CanEditServers],
            (context, input, token) =>
                throw new QuasarOperationException(409, "The server is busy.")
        );
        services.AddQuasarOperation<JsonElement, object?>(
            Plugin,
            "servers.slow",
            [QuasarPolicyNames.CanView],
            async (context, input, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return null;
            },
            new() { Deadline = TimeSpan.FromSeconds(1) }
        );
        _descriptors = [.. services];
        _services = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _services.Dispose();
        _rbac.Dispose();
        _cache.SetValue(null, _previous);
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException) { }
    }

    [Fact]
    public async Task BothAgentsAreRefusedEveryHardDeniedOperation()
    {
        var admin = Person(QuasarRoles.Admin);
        foreach (var agent in new[] { await AssistantAsync(admin), Player() })
        {
            foreach (
                var operation in DenyPatternOperations.Concat([
                    "servers.secret",
                    "admin.read",
                    "power.read",
                ])
            )
            {
                var result = await CallAsync(agent, operation);
                Assert.Equal(403, result.StatusCode);
                Assert.Equal(AgentDenied, result.Error);
            }
        }
        Assert.Equal(200, (await CallAsync(admin, "llm.keys.read")).StatusCode);
        var assistant = await AssistantAsync(admin);
        Assert.Equal(200, (await CallAsync(assistant, "servers.read")).StatusCode);

        // A hand-edited rule that adds security administration closes the operation to agents, never opens it.
        await _rbac.UpdateAsync(config =>
        {
            config.OperationOverrides[Plugin + "/servers.read"] = new()
            {
                RequiredPolicies = [QuasarPolicyNames.CanView, QuasarPolicyNames.CanManageSecurity],
            };
            return Task.CompletedTask;
        });
        Assert.Equal(AgentDenied, (await CallAsync(assistant, "servers.read")).Error);
        Assert.Equal(AgentDenied, (await CallAsync(Player(), "servers.read")).Error);
        Assert.Equal(200, (await CallAsync(admin, "servers.read")).StatusCode);
    }

    [Fact]
    public async Task AgentAvailabilityFollowsTheDenyListAndTheEffectiveRule()
    {
        await using var scope = _services.CreateAsyncScope();
        var agents = scope.ServiceProvider.GetRequiredService<QuasarOperationService>();
        Assert.True(await agents.IsAvailableToAgentsAsync(Plugin, "servers.read"));
        foreach (
            var operation in DenyPatternOperations.Concat([
                "servers.secret",
                "admin.read",
                "power.read",
                "missing",
            ])
        )
            Assert.False(await agents.IsAvailableToAgentsAsync(Plugin, operation), operation);

        await _rbac.UpdateAsync(config =>
        {
            config.OperationOverrides[Plugin + "/servers.read"] = new()
            {
                RequiredPolicies = [QuasarPolicyNames.CanView, QuasarPolicyNames.CanManageSecurity],
            };
            config.OperationOverrides[Plugin + "/servers.set"] = new()
            {
                Enabled = false,
                RequiredPolicies = [QuasarPolicyNames.CanEditServers],
            };
            return Task.CompletedTask;
        });
        Assert.False(await agents.IsAvailableToAgentsAsync(Plugin, "servers.read"));
        Assert.False(await agents.IsAvailableToAgentsAsync(Plugin, "servers.set"));
        Assert.True(await agents.IsAvailableToAgentsAsync(Plugin, "my.grids"));
    }

    [Fact]
    public async Task AssistantNeedsBothItsRoleAndTheAskersPolicies()
    {
        var editorAssistant = await AssistantAsync(Person(QuasarRoles.Editor));
        Assert.Equal(
            200,
            (await CallAsync(editorAssistant, "servers.set", new SetRequest(1))).StatusCode
        );

        await SetPolicyRolesAsync(
            QuasarPolicyNames.CanUseAssistant,
            [QuasarRoles.Viewer, QuasarRoles.Editor, QuasarRoles.Admin]
        );
        var viewerAssistant = await AssistantAsync(Person(QuasarRoles.Viewer));
        Assert.Equal(200, (await CallAsync(viewerAssistant, "servers.read")).StatusCode);
        var refused = await CallAsync(viewerAssistant, "servers.set", new SetRequest(2));
        Assert.Equal(403, refused.StatusCode);
        Assert.Contains("asking the assistant", refused.Error);

        await Operations(operations =>
            operations.SaveAgentRolesAsync(
                Person(QuasarRoles.Admin),
                QuasarAgentKind.Assistant,
                [QuasarRoles.Viewer]
            )
        );
        var adminAskedViewerAssistant = await AssistantAsync(Person(QuasarRoles.Admin));
        Assert.Equal(
            403,
            (
                await CallAsync(adminAskedViewerAssistant, "servers.set", new SetRequest(3))
            ).StatusCode
        );
        Assert.Equal(1, _value);
    }

    [Fact]
    public async Task AdminIsNeverAnAgentRole()
    {
        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            Operations(operations =>
                operations.SaveAgentRolesAsync(
                    Person(QuasarRoles.Admin),
                    QuasarAgentKind.Assistant,
                    [QuasarRoles.Admin]
                )
            )
        );
        Assert.Contains("admin", error.Message);
        await _rbac.UpdateAsync(config =>
        {
            config.AgentRoles["assistant"] = [QuasarRoles.Admin, QuasarRoles.Editor];
            config.AgentRoles["player"] = [QuasarRoles.Admin];
            return Task.CompletedTask;
        });
        Assert.Equal(new[] { QuasarRoles.Editor }, _rbac.GetConfig().AgentRoles["assistant"]);
        Assert.Empty(_rbac.GetConfig().AgentRoles["player"]);
        var assistant = await AssistantAsync(Person(QuasarRoles.Admin));
        Assert.False(assistant.IsInRole(QuasarRoles.Admin));
        Assert.True(assistant.IsInRole(QuasarRoles.Editor));
        Assert.True(assistant.IsQuasarAgent(out var identity));
        Assert.True(identity.Asker!.IsInRole(QuasarRoles.Admin));
        Assert.False(Player().IsInRole(QuasarRoles.Admin));

        await File.WriteAllTextAsync(Path.Combine(_root, "rbac.json"), """{"agentRoles": null}""");
        using var reloaded = new RbacConfigCatalog(NullLogger<RbacConfigCatalog>.Instance);
        Assert.True(reloaded.GetConfig().SdkRulesValid);
        Assert.Equal(new[] { QuasarRoles.Editor }, reloaded.GetConfig().AgentRoles["assistant"]);
    }

    [Fact]
    public async Task OnlyAPersonWhoMayUseTheAssistantCanAskIt()
    {
        var principals = _services.GetRequiredService<QuasarAgentPrincipals>();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            principals.CreateAssistantAsync(Person(QuasarRoles.Viewer), "c", "r")
        );
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            principals.CreateAssistantAsync(new ClaimsPrincipal(new ClaimsIdentity()), "c", "r")
        );
        var service = new ClaimsPrincipal(
            new ClaimsIdentity(
                [
                    new Claim(QuasarClaimTypes.Provider, QuasarAuthSchemes.ServicePrincipal),
                    new Claim(ClaimTypes.Role, QuasarRoles.Admin),
                ],
                QuasarAuthSchemes.ServicePrincipal
            )
        );
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            principals.CreateAssistantAsync(service, "c", "r")
        );
        var assistant = await AssistantAsync(Person(QuasarRoles.Admin));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            principals.CreateAssistantAsync(assistant, "c", "r")
        );
        Assert.Throws<ArgumentException>(() =>
            principals.CreatePlayer("not-a-steam-id", null, null, "c", "r")
        );
    }

    [Fact]
    public async Task OnlyThePlayerAgentActsAsPlayer()
    {
        Assert.Equal(200, (await CallAsync(Player(), "my.grids")).StatusCode);
        Assert.Equal(403, (await CallAsync(Player(), "servers.read")).StatusCode);
        var admin = Person(QuasarRoles.Admin);
        Assert.Equal(403, (await CallAsync(admin, "my.grids")).StatusCode);
        Assert.Equal(403, (await CallAsync(await AssistantAsync(admin), "my.grids")).StatusCode);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            SetPolicyRolesAsync(QuasarPolicyNames.CanActAsPlayer, [QuasarRoles.Admin])
        );
        await _rbac.UpdateAsync(config =>
        {
            config.PolicyOverrides[QuasarPolicyNames.CanActAsPlayer] =
            [
                QuasarRoles.Admin,
                QuasarRoles.Viewer,
            ];
            return Task.CompletedTask;
        });
        Assert.Equal(403, (await CallAsync(admin, "my.grids")).StatusCode);
    }

    [Fact]
    public async Task ClaimsThatLookLikeAnAgentAreNeitherAgentNorAdmin()
    {
        var fake = new ClaimsPrincipal(
            new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, "agent:player"),
                    new Claim(QuasarAgentIdentity.ProviderClaimType, QuasarAgentIdentity.Provider),
                    new Claim(ClaimTypes.Role, QuasarRoles.Admin),
                ],
                QuasarAgentIdentity.Provider
            )
        );
        Assert.False(fake.IsQuasarAgent(out _));
        var refreshed = _services.GetRequiredService<QuasarRoleMapper>().RefreshRoles(fake);
        Assert.False(refreshed.IsInRole(QuasarRoles.Admin));
        Assert.False(refreshed.IsQuasarAgent(out _));
        Assert.Equal(403, (await CallAsync(fake, "admin.read")).StatusCode);
        Assert.Equal(403, (await CallAsync(fake, "my.grids")).StatusCode);
        Assert.Equal(403, (await CallAsync(fake, "servers.read")).StatusCode);
    }

    [Fact]
    public async Task ChangesAreAuditedAndReadsAreNot()
    {
        var editor = Person(QuasarRoles.Editor, "editor-1");
        Assert.Equal(200, (await CallAsync(editor, "servers.read")).StatusCode);
        Assert.Equal(
            200,
            (
                await CallAsync(
                    editor,
                    "servers.set",
                    new
                    {
                        value = 4,
                        password = "hunter2",
                        apiKey = "k",
                        nested = new { authToken = "t", name = "kept" },
                    }
                )
            ).StatusCode
        );
        Assert.Equal(409, (await CallAsync(editor, "servers.fail")).StatusCode);
        Assert.Equal(
            403,
            (
                await CallAsync(Person(QuasarRoles.Viewer), "servers.set", new SetRequest(5))
            ).StatusCode
        );
        Assert.Equal(
            200,
            (await CallAsync(editor, "servers.set", new SetRequest(6, Skip: true))).StatusCode
        );

        var entries = await QueryAsync();
        Assert.Equal(4, entries.Count);
        Assert.DoesNotContain(entries, entry => entry.Operation.EndsWith("servers.read"));
        var applied = entries.Single(entry => entry.UndoData is not null);
        Assert.Equal(
            (QuasarAuditPrincipalKind.Person, "editor-1"),
            (applied.Principal.Kind, applied.Principal.Subject)
        );
        Assert.Equal("editor-1", applied.Requester.Subject);
        Assert.Equal(
            (QuasarAuditMode.Applied, QuasarAuditResult.Ok, QuasarReversal.Undo),
            (applied.Mode, applied.Result, applied.Reversal)
        );
        Assert.Equal(("alpha", "servers"), (applied.Server, applied.Area));
        var arguments = applied.Arguments!.Value;
        Assert.Equal("<redacted>", arguments.GetProperty("password").GetString());
        Assert.Equal("<redacted>", arguments.GetProperty("apiKey").GetString());
        Assert.Equal(
            "<redacted>",
            arguments.GetProperty("nested").GetProperty("authToken").GetString()
        );
        Assert.Equal("kept", arguments.GetProperty("nested").GetProperty("name").GetString());
        Assert.Equal(4, arguments.GetProperty("value").GetInt32());
        var failed = entries.Single(entry => entry.Result == QuasarAuditResult.Failed);
        Assert.Equal(
            ("The server is busy.", QuasarReversal.NotReversible),
            (failed.Error, failed.Reversal)
        );
        Assert.Single(entries, entry => entry.Result == QuasarAuditResult.Refused);
        // An undoable operation that recorded nothing to undo is not reversible.
        Assert.Single(
            entries,
            entry =>
                entry.Result == QuasarAuditResult.Ok
                && entry.UndoData is null
                && entry.Reversal == QuasarReversal.NotReversible
        );
        Assert.Equal(2, (await QueryAsync(new() { Result = QuasarAuditResult.Ok })).Count);
        Assert.Single(await QueryAsync(new() { Result = QuasarAuditResult.Ok, Take = 1 }));
        Assert.Empty(
            await QueryAsync(new() { PrincipalKind = QuasarAuditPrincipalKind.Assistant })
        );

        // A transport's own check before dispatching audits a refused change too, but not a refused read.
        await Operations(async operations =>
        {
            Assert.NotNull(
                await operations.CheckCallAsync(
                    Person(QuasarRoles.Viewer),
                    Plugin,
                    "servers.set",
                    QuasarOperationKind.Json
                )
            );
            Assert.NotNull(
                await operations.CheckCallAsync(
                    Person(QuasarRoles.Viewer),
                    Plugin,
                    "admin.read",
                    QuasarOperationKind.Json
                )
            );
        });
        Assert.Equal(2, (await QueryAsync(new() { Result = QuasarAuditResult.Refused })).Count);
    }

    [Fact]
    public async Task ApprovalsAndProposalsAreRecordedAndTheApproverIsChecked()
    {
        var asker = Person(QuasarRoles.Editor, "asker");
        var assistant = await _services
            .GetRequiredService<QuasarAgentPrincipals>()
            .CreateAssistantAsync(asker, "conversation-7", "set it to 8");
        var audit = _services.GetRequiredService<QuasarAuditLog>();
        var proposal = await audit.RecordProposalAsync(
            assistant,
            Plugin,
            "servers.set",
            JsonSerializer.SerializeToElement(new SetRequest(8)),
            "asked to",
            default
        );
        Assert.Equal((QuasarAuditMode.Proposed, "asked to"), (proposal.Mode, proposal.Reason));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            audit.RecordProposalAsync(asker, Plugin, "servers.set", default, null, default)
        );
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            audit.RecordProposalAsync(assistant, Plugin, "llm.keys.read", default, null, default)
        );

        var refused = await CallAsync(
            assistant,
            "servers.set",
            new SetRequest(8),
            approver: Person(QuasarRoles.Viewer, "viewer")
        );
        Assert.Equal(403, refused.StatusCode);
        Assert.Contains("approver", refused.Error);
        Assert.Equal(
            200,
            (
                await CallAsync(
                    assistant,
                    "servers.set",
                    new SetRequest(8),
                    approver: Person(QuasarRoles.Admin, "approver")
                )
            ).StatusCode
        );
        Assert.Equal(8, _value);

        var entries = await QueryAsync(
            new() { PrincipalKind = QuasarAuditPrincipalKind.Assistant }
        );
        Assert.Equal(3, entries.Count);
        var approved = entries.Single(entry =>
            entry.Mode == QuasarAuditMode.Approved && entry.Result == QuasarAuditResult.Ok
        );
        Assert.Equal(
            ("approver", "asker", "conversation-7", "set it to 8"),
            (
                approved.ApprovedBy,
                approved.Requester.Subject,
                approved.ConversationId,
                approved.Request
            )
        );
        Assert.Equal("agent:assistant", approved.Principal.Subject);
        Assert.Single(
            await QueryAsync(new() { Requester = "ASKER", Result = QuasarAuditResult.Refused })
        );
    }

    [Fact]
    public async Task ApprovalRunsTheProposalAsTheAgentOnce()
    {
        const string askerId = "76561198000000101";
        const string adminId = "76561198000000102";
        const string viewerId = "76561198000000103";
        await MapSteamAsync(askerId, QuasarRoles.Editor);
        await MapSteamAsync(adminId, QuasarRoles.Admin);
        await MapSteamAsync(viewerId, QuasarRoles.Viewer);
        var audit = _services.GetRequiredService<QuasarAuditLog>();
        var assistant = await AssistantAsync(Steam(askerId));
        var proposal = await audit.RecordProposalAsync(
            assistant,
            Plugin,
            "servers.set",
            JsonSerializer.SerializeToElement(new SetRequest(7)),
            "asked",
            default
        );

        var refused = await audit.ApproveAsync(Steam(viewerId), proposal.Id, default);
        Assert.Equal(403, refused.StatusCode);
        Assert.Equal(0, _value);
        Assert.Equal(
            200,
            (await audit.ApproveAsync(Steam(adminId), proposal.Id, default)).StatusCode
        );
        Assert.Equal(7, _value);
        var approved = (await QueryAsync()).Single(entry =>
            entry.Mode == QuasarAuditMode.Approved && entry.Result == QuasarAuditResult.Ok
        );
        Assert.Equal(proposal.Id, approved.Proposal);
        Assert.Equal(QuasarAuditPrincipalKind.Assistant, approved.Principal.Kind);
        Assert.Equal(askerId, approved.Requester.Subject);
        Assert.Equal(adminId, approved.ApprovedBy);
        Assert.Equal("conversation", approved.ConversationId);
        Assert.Equal(
            409,
            (await audit.ApproveAsync(Steam(adminId), proposal.Id, default)).StatusCode
        );
        Assert.Equal(
            409,
            (await audit.RejectAsync(Steam(adminId), proposal.Id, null, default)).StatusCode
        );
        Assert.Equal(
            409,
            (await audit.ApproveAsync(Steam(adminId), approved.Id, default)).StatusCode
        );
        Assert.Equal(
            404,
            (await audit.ApproveAsync(Steam(adminId), "missing", default)).StatusCode
        );

        // The asker is checked again at approval time.
        var second = await audit.RecordProposalAsync(
            assistant,
            Plugin,
            "servers.set",
            JsonSerializer.SerializeToElement(new SetRequest(8)),
            null,
            default
        );
        await MapSteamAsync(askerId, QuasarRoles.Viewer);
        Assert.Equal(
            403,
            (await audit.ApproveAsync(Steam(adminId), second.Id, default)).StatusCode
        );
        Assert.Equal(7, _value);
    }

    [Fact]
    public async Task RejectedProposalsCannotBeApproved()
    {
        const string askerId = "76561198000000111";
        const string adminId = "76561198000000112";
        await MapSteamAsync(askerId, QuasarRoles.Editor);
        await MapSteamAsync(adminId, QuasarRoles.Admin);
        var audit = _services.GetRequiredService<QuasarAuditLog>();
        var assistant = await AssistantAsync(Steam(askerId));
        var arguments = JsonSerializer.SerializeToElement(new SetRequest(3));
        var proposal = await audit.RecordProposalAsync(
            assistant,
            Plugin,
            "servers.set",
            arguments,
            null,
            default
        );
        var expiring = await audit.RecordProposalAsync(
            assistant,
            Plugin,
            "servers.set",
            arguments,
            null,
            default
        );

        Assert.Equal(
            403,
            (await audit.RejectAsync(assistant, proposal.Id, "no", default)).StatusCode
        );
        var rejected = await audit.RejectAsync(Steam(adminId), proposal.Id, " not now ", default);
        Assert.Equal(200, rejected.StatusCode);
        var entry = Assert.IsType<QuasarAuditEntry>(rejected.Value);
        Assert.Equal(QuasarAuditMode.Rejected, entry.Mode);
        Assert.Equal(proposal.Id, entry.Proposal);
        Assert.Equal("not now", entry.Error);
        Assert.Equal(adminId, entry.ApprovedBy);
        Assert.Equal(
            409,
            (await audit.ApproveAsync(Steam(adminId), proposal.Id, default)).StatusCode
        );

        var expired = Assert.IsType<QuasarAuditEntry>(
            (await audit.RejectAsync(null, expiring.Id, "ignored", default)).Value
        );
        Assert.Equal("expired", expired.Error);
        Assert.Null(expired.ApprovedBy);
        Assert.Equal(
            409,
            (await audit.ApproveAsync(Steam(adminId), expiring.Id, default)).StatusCode
        );
        Assert.Equal(0, _value);
    }

    [Fact]
    public async Task ApprovedPlayerProposalRunsAsTheSamePlayer()
    {
        const string adminId = "76561198000000121";
        await MapSteamAsync(adminId, QuasarRoles.Admin);
        ClaimsPrincipal? caller = null;
        var services = new ServiceCollection();
        foreach (var descriptor in _descriptors)
            ((IList<ServiceDescriptor>)services).Add(descriptor);
        services.AddQuasarOperation<JsonElement, object?>(
            Plugin,
            "my.restore",
            [QuasarPolicyNames.CanActAsPlayer],
            (context, input, token) =>
            {
                caller = context.User;
                return Task.FromResult<object?>(null);
            },
            new() { Reversal = QuasarReversal.NothingToReverse }
        );
        await using var provider = services.BuildServiceProvider();
        var audit = provider.GetRequiredService<QuasarAuditLog>();
        var player = provider
            .GetRequiredService<QuasarAgentPrincipals>()
            .CreatePlayer("76561198000000001", "Pilot", "42", "player-conversation", "!ask");
        var proposal = await audit.RecordProposalAsync(
            player,
            Plugin,
            "my.restore",
            JsonSerializer.SerializeToElement(new { grid = 1 }),
            null,
            default
        );

        Assert.Equal(
            200,
            (await audit.ApproveAsync(Steam(adminId), proposal.Id, default)).StatusCode
        );
        Assert.True(caller!.IsQuasarAgent(out var agent));
        Assert.Equal(QuasarAgentKind.Player, agent.Kind);
        Assert.Equal("76561198000000001", agent.PlayerSteamId);
        Assert.Equal("Pilot", agent.PlayerName);
        Assert.Equal("42", agent.DiscordUserId);
        Assert.Equal("player-conversation", agent.ConversationId);
    }

    [Fact]
    public async Task UndoChecksTheStateRefusesTwiceAndNeedsTheOriginalPolicy()
    {
        var admin = Person(QuasarRoles.Admin, "admin-1");
        var audit = _services.GetRequiredService<QuasarAuditLog>();
        await CallAsync(Person(QuasarRoles.Editor), "servers.set", new SetRequest(5));
        var first = (await QueryAsync()).Single();

        Assert.Equal(
            QuasarUndoStatus.Refused,
            (await audit.UndoAsync(Person(QuasarRoles.Editor), first.Id, false, default)).Status
        );
        var done = await audit.UndoAsync(admin, first.Id, false, default);
        Assert.Equal(QuasarUndoStatus.Done, done.Status);
        Assert.Equal(0, _value);
        var reversal = (await QueryAsync()).First();
        Assert.Equal(
            (first.Id, "admin-1", QuasarReversal.NotReversible),
            (reversal.Reverses, reversal.Principal.Subject, reversal.Reversal)
        );
        var again = await audit.UndoAsync(admin, first.Id, false, default);
        Assert.Equal(
            (QuasarUndoStatus.Refused, "This change has already been undone."),
            (again.Status, again.Detail)
        );

        await CallAsync(Person(QuasarRoles.Editor), "servers.set", new SetRequest(7));
        var second = (await QueryAsync()).First();
        _value = 9;
        var conflict = await audit.UndoAsync(admin, second.Id, false, default);
        Assert.Equal(
            (QuasarUndoStatus.Conflict, "value is 9, not 7"),
            (conflict.Status, conflict.Detail)
        );
        Assert.Equal(9, _value);
        Assert.Equal(
            QuasarUndoStatus.Done,
            (await audit.UndoAsync(admin, second.Id, true, default)).Status
        );
        Assert.Equal(0, _value);

        await CallAsync(Person(QuasarRoles.Editor), "servers.set", new SetRequest(3));
        var third = (await QueryAsync()).First();
        await _rbac.UpdateAsync(config =>
        {
            config.OperationOverrides[Plugin + "/servers.set"] = new()
            {
                RequiredPolicies = [QuasarPolicyNames.CanActAsPlayer],
            };
            return Task.CompletedTask;
        });
        Assert.Equal(
            QuasarUndoStatus.Refused,
            (await audit.UndoAsync(admin, third.Id, false, default)).Status
        );
        Assert.Equal(3, _value);
    }

    [Fact]
    public async Task AuditPageShowsAReversalAsTheUndoOfItsChange()
    {
        var admin = Person(QuasarRoles.Admin);
        var audit = _services.GetRequiredService<QuasarAuditLog>();
        await CallAsync(Person(QuasarRoles.Editor), "servers.set", new SetRequest(5));
        var change = (await QueryAsync()).Single();
        Assert.Equal(
            QuasarUndoStatus.Done,
            (await audit.UndoAsync(admin, change.Id, false, default)).Status
        );
        await CallAsync(Person(QuasarRoles.Editor), "servers.set", new SetRequest(6));

        await using var context = new BunitContext();
        context.Services.AddMudServices();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddSingleton(audit);
        context.Services.AddSingleton<AuthenticationStateProvider>(new SignedIn(admin));
        var page = context.Render<Audit>();
        // The page loads the entries asynchronously. Newest first: the later change, the reversal,
        // the undone change.
        page.WaitForAssertion(() => Assert.Equal(3, page.FindAll("tbody > tr").Count));
        var rows = page.FindAll("tbody > tr");
        Assert.Contains(
            "Undo",
            rows[0].QuerySelectorAll("button").Select(b => b.TextContent.Trim())
        );
        Assert.Contains(
            "Undo of " + change.Time.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss"),
            rows[1].TextContent
        );
        Assert.Contains("Undone", rows[2].TextContent);
        Assert.DoesNotContain(
            "Undo",
            rows[2].QuerySelectorAll("button").Select(b => b.TextContent.Trim())
        );
    }

    [Fact]
    public async Task UndoRestoresBlobsUntilTheyExpire()
    {
        var admin = Person(QuasarRoles.Admin);
        var audit = _services.GetRequiredService<QuasarAuditLog>();
        await CallAsync(Person(QuasarRoles.Editor), "files.set");
        await CallAsync(Person(QuasarRoles.Editor), "files.set");
        var entries = await QueryAsync();
        Assert.All(entries, entry => Assert.True(entry.HasBlobs));
        Assert.Equal(
            QuasarUndoStatus.Done,
            (await audit.UndoAsync(admin, entries[0].Id, false, default)).Status
        );
        Assert.Equal("old content", _restoredBlob);

        Directory.Delete(audit.BlobDirectory(entries[1].Id), recursive: true);
        var expired = await audit.UndoAsync(admin, entries[1].Id, false, default);
        Assert.Equal(
            (QuasarUndoStatus.Refused, "The undo data of this change has expired."),
            (expired.Status, expired.Detail)
        );
    }

    [Fact]
    public async Task RetentionDropsOldBlobsAndMonthFiles()
    {
        await CallAsync(Person(QuasarRoles.Editor), "files.set");
        var audit = _services.GetRequiredService<QuasarAuditLog>();
        var entry = (await QueryAsync()).Single();
        audit.Prune(DateTimeOffset.UtcNow);
        Assert.True(Directory.Exists(audit.BlobDirectory(entry.Id)));
        audit.Prune(DateTimeOffset.UtcNow.AddDays(QuasarAuditOptions.DefaultRetentionDays + 1));
        Assert.False(Directory.Exists(audit.BlobDirectory(entry.Id)));
        audit.Prune(DateTimeOffset.UtcNow.AddDays(QuasarAuditOptions.DefaultRetentionDays + 32));
        Assert.Empty(await QueryAsync());
    }

    [Fact]
    public async Task MetadataDefaultsAndDeadlines()
    {
        Assert.Equal(
            QuasarOperationEffect.Change,
            new QuasarOperationMetadata().ResolveFor(QuasarOperationKind.Json, "a.b").Effect
        );
        Assert.Equal(
            QuasarOperationEffect.Change,
            new QuasarOperationMetadata().ResolveFor(QuasarOperationKind.Upload, "a.b").Effect
        );
        Assert.Equal(
            QuasarOperationEffect.Read,
            new QuasarOperationMetadata().ResolveFor(QuasarOperationKind.Read, "a.b").Effect
        );
        Assert.Equal(
            QuasarOperationEffect.Read,
            new QuasarOperationMetadata().ResolveFor(QuasarOperationKind.Subscription, "a.b").Effect
        );
        Assert.Equal(
            "servers",
            new QuasarOperationMetadata().ResolveFor(QuasarOperationKind.Json, "servers.start").Area
        );
        Assert.Equal(
            QuasarReversal.NotReversible,
            new QuasarOperationMetadata().ResolveFor(QuasarOperationKind.Json, "a").Reversal
        );
        foreach (var seconds in new[] { 0.5, 301 })
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new ServiceCollection().AddQuasarOperation<JsonElement, object?>(
                    Plugin,
                    "x",
                    [QuasarPolicyNames.CanView],
                    (_, _, _) => Task.FromResult<object?>(null),
                    new() { Deadline = TimeSpan.FromSeconds(seconds) }
                )
            );
        Assert.Throws<ArgumentException>(() =>
            new QuasarOperationMetadata { Reversal = QuasarReversal.Undo }.ResolveFor(
                QuasarOperationKind.Json,
                "a"
            )
        );
        Assert.Throws<ArgumentException>(() =>
            new QuasarOperationMetadata { Undo = new ValueUndo(this) }.ResolveFor(
                QuasarOperationKind.Json,
                "a"
            )
        );
        var core = new ServiceCollection();
        core.AddQuasarCoreOperations();
        var operations = core.BuildServiceProvider()
            .GetServices<QuasarOperation>()
            .ToDictionary(operation => operation.OperationId);
        foreach (
            var read in new[]
            {
                "backup.read",
                "analytics.series.read",
                "logs.server.read",
                "logs.magnetar.read",
                "logs.discord.read",
            }
        )
            Assert.Equal(QuasarOperationEffect.Read, operations[read].Metadata.Effect);
        Assert.Equal(QuasarAgentAccess.Denied, operations["backup.read"].Metadata.AgentAccess);
        Assert.Equal(
            QuasarAgentAccess.Denied,
            operations["logs.discord.read"].Metadata.AgentAccess
        );

        var started = DateTime.UtcNow;
        var slow = await CallAsync(Person(QuasarRoles.Viewer), "servers.slow");
        Assert.Equal(504, slow.StatusCode);
        Assert.InRange(
            DateTime.UtcNow - started,
            TimeSpan.FromSeconds(0.9),
            TimeSpan.FromSeconds(20)
        );
    }

    private static void Add(
        IServiceCollection services,
        string operationId,
        string[] policies,
        QuasarOperationMetadata metadata
    ) =>
        services.AddQuasarOperation<JsonElement, object>(
            Plugin,
            operationId,
            policies,
            (context, input, token) => Task.FromResult<object>(new { ok = true }),
            metadata
        );

    private static ClaimsPrincipal Person(string role, string subject = "person") =>
        new(
            new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, subject), new Claim(ClaimTypes.Role, role)],
                "test"
            )
        );

    private ClaimsPrincipal Steam(string steamId) =>
        _services.GetRequiredService<QuasarRoleMapper>().CreateSteamPrincipal(steamId);

    private Task MapSteamAsync(string steamId, string role) =>
        _rbac.UpdateAsync(config =>
        {
            config.SubjectRoleMappings.RemoveAll(mapping => mapping.Subject == steamId);
            config.SubjectRoleMappings.Add(new() { Subject = steamId, Roles = [role] });
            return Task.CompletedTask;
        });

    private Task<ClaimsPrincipal> AssistantAsync(ClaimsPrincipal asker) =>
        _services
            .GetRequiredService<QuasarAgentPrincipals>()
            .CreateAssistantAsync(asker, "conversation", "request");

    private ClaimsPrincipal Player() =>
        _services
            .GetRequiredService<QuasarAgentPrincipals>()
            .CreatePlayer("76561198000000001", "Pilot", null, "player-conversation", "!ask");

    private async Task<QuasarOperationResult> CallAsync(
        ClaimsPrincipal user,
        string operationId,
        object? request = null,
        ClaimsPrincipal? approver = null
    )
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope
            .ServiceProvider.GetRequiredService<QuasarOperationService>()
            .DispatchAsync(
                new(user, scope.ServiceProvider) { ApprovedBy = approver },
                Plugin,
                operationId,
                JsonSerializer.SerializeToElement(request ?? new { }),
                default
            );
    }

    private async Task Operations(Func<QuasarOperationService, Task> action)
    {
        await using var scope = _services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<QuasarOperationService>());
    }

    private Task SetPolicyRolesAsync(string policy, IReadOnlyList<string> roles) =>
        Operations(operations =>
            operations.SavePolicyRolesAsync(Person(QuasarRoles.Admin), policy, roles)
        );

    private Task<IReadOnlyList<QuasarAuditEntry>> QueryAsync(QuasarAuditFilter? filter = null) =>
        _services.GetRequiredService<QuasarAuditLog>().QueryAsync(filter ?? new(), default);

    private sealed record SetRequest(int Value, bool Skip = false);

    private sealed class SignedIn(ClaimsPrincipal user) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(user));
    }

    private sealed class ValueUndo(QuasarAgentAuditTests owner) : IQuasarUndoHandler
    {
        public Task<string?> CheckAsync(QuasarUndoContext context, CancellationToken token)
        {
            var after = context.Data.GetProperty("after").GetInt32();
            return Task.FromResult(
                owner._value == after ? null : $"value is {owner._value}, not {after}"
            );
        }

        public Task ReverseAsync(QuasarUndoContext context, CancellationToken token)
        {
            owner._value = context.Data.GetProperty("before").GetInt32();
            return Task.CompletedTask;
        }
    }

    private sealed class BlobUndo(QuasarAgentAuditTests owner) : IQuasarUndoHandler
    {
        public Task<string?> CheckAsync(QuasarUndoContext context, CancellationToken token) =>
            Task.FromResult<string?>(null);

        public async Task ReverseAsync(QuasarUndoContext context, CancellationToken token) =>
            owner._restoredBlob = await File.ReadAllTextAsync(
                Path.Combine(context.BlobDirectory, "old.txt"),
                token
            );
    }
}
