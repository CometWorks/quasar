using System.Reflection;
using Discord;
using Microsoft.Extensions.Logging.Abstractions;
using Quasar.Plugin.Abstractions.Commands;
using Quasar.Plugin.Abstractions.Discord;
using Quasar.Services.Discord;
using Xunit;

namespace Quasar.Tests;

public class DiscordPluginInteractionsTests
{
    [Fact]
    public async Task ApplicationCommandsRouteByTypeAndKeepExistingSlashCommands()
    {
        var slash = new Command(() => Slash("demo"));
        var user = new Command(() => new UserCommandBuilder().WithName("demo").Build());
        var message = new Command(() => new MessageCommandBuilder().WithName("demo").Build());
        var duplicate = new Command(() => Slash("demo"));
        var core = new Command(() => Slash("whisper"));
        var legacy = new Command(() => Slash("ask"));
        var incomplete = new Command(() => new SlashCommandBuilder().WithName("incomplete").Build());
        var invalid = new Command(() => throw new InvalidOperationException("Invalid definition"));
        var interactions = new DiscordPluginInteractions(
            [new QuasarChatCommandInfo("ask", "Ask a question", "question", "Question")],
            [invalid, incomplete, core, legacy, slash, user, message, duplicate], [], NullLogger.Instance);

        Assert.Equal(3, interactions.Definitions.Count);
        var data = Stub<IApplicationCommandInteractionData>("get_Name", "demo");
        await interactions.DispatchAsync(Stub<ISlashCommandInteraction>("get_Data", data), default);
        await interactions.DispatchAsync(Stub<IUserCommandInteraction>("get_Data", data), default);
        await interactions.DispatchAsync(Stub<IMessageCommandInteraction>("get_Data", data), default);
        var autocomplete = Stub<IAutocompleteInteractionData>("get_CommandName", "demo");
        await interactions.DispatchAsync(Stub<IAutocompleteInteraction>("get_Data", autocomplete), default);

        Assert.Equal(1, slash.Calls);
        Assert.Equal(1, slash.AutocompleteCalls);
        Assert.Equal(1, user.Calls);
        Assert.Equal(1, message.Calls);
        Assert.Equal(0, duplicate.Calls);
    }

    [Fact]
    public async Task ComponentsAndModalsRouteOnlyToTheirExactPrefix()
    {
        var handler = new Handler("demo");
        var duplicate = new Handler("demo");
        var interactions = new DiscordPluginInteractions([], [], [handler, duplicate], NullLogger.Instance);

        var button = Stub<IComponentInteractionData>("get_CustomId", "demo:refresh:123");
        var modal = Stub<IModalInteractionData>("get_CustomId", "demo:submit");
        var other = Stub<IComponentInteractionData>("get_CustomId", "demo-extra:refresh");
        var emptyAction = Stub<IComponentInteractionData>("get_CustomId", "demo:");
        await interactions.DispatchAsync(Stub<IComponentInteraction>("get_Data", button), default);
        await interactions.DispatchAsync(Stub<IModalInteraction>("get_Data", modal), default);
        await interactions.DispatchAsync(Stub<IComponentInteraction>("get_Data", other), default);
        await interactions.DispatchAsync(Stub<IComponentInteraction>("get_Data", emptyAction), default);

        Assert.Equal(2, handler.Calls);
        Assert.Equal(0, duplicate.Calls);
    }

    private static SlashCommandProperties Slash(string name) =>
        new SlashCommandBuilder().WithName(name).WithDescription("Test plugin command").Build();

    private static T Stub<T>(string getter, object value) where T : class
    {
        var stub = DispatchProxy.Create<T, DiscordLifecycleCommandTests.DiscordProxy>();
        ((DiscordLifecycleCommandTests.DiscordProxy)(object)stub).Handler = (method, _) =>
            method.Name == getter ? value : throw new NotSupportedException(method.Name);
        return stub;
    }

    private sealed class Command(Func<ApplicationCommandProperties> build) : IQuasarDiscordCommand
    {
        public int Calls { get; private set; }
        public int AutocompleteCalls { get; private set; }

        public ApplicationCommandProperties BuildCommand() => build();

        public Task HandleAsync(IApplicationCommandInteraction interaction, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.CompletedTask;
        }

        public Task HandleAutocompleteAsync(IAutocompleteInteraction interaction, CancellationToken cancellationToken)
        {
            AutocompleteCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class Handler(string prefix) : IQuasarDiscordInteractionHandler
    {
        public string CustomIdPrefix => prefix;
        public int Calls { get; private set; }

        public Task HandleAsync(IDiscordInteraction interaction, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }
}
