using System.Reflection;
using Discord;
using Quasar.Services.Assistant;
using Quasar.Services.Discord;
using Xunit;

namespace Quasar.Tests;

public sealed class QuasarDiscordChannelsTests
{
    private const ulong Guild = 100;

    private sealed record Sent(string Text, AllowedMentions? Mentions);

    [Fact]
    public async Task PostsOnlyToTheConfiguredGuildWithoutMentionsAndSplitsLongText()
    {
        var sent = new List<Sent>();
        var limiter = new DiscordRateLimiter();
        var channel = TextChannel(7, Guild, sent);

        Assert.True(
            await QuasarDiscordChannels.PostAsync(
                channel,
                Guild,
                "@everyone approve this",
                limiter,
                default
            )
        );
        var post = Assert.Single(sent);
        Assert.Equal("@everyone approve this", post.Text);
        Assert.Same(AllowedMentions.None, post.Mentions);

        sent.Clear();
        var paragraph = new string('a', 1500);
        Assert.True(
            await QuasarDiscordChannels.PostAsync(
                channel,
                Guild,
                paragraph + "\n\n" + paragraph,
                limiter,
                default
            )
        );
        Assert.Equal(new[] { paragraph, paragraph }, sent.Select(s => s.Text));
        Assert.All(sent, s => Assert.Same(AllowedMentions.None, s.Mentions));

        sent.Clear();
        Assert.False(
            await QuasarDiscordChannels.PostAsync(
                TextChannel(8, 200, sent),
                Guild,
                "hi",
                limiter,
                default
            )
        );
        Assert.False(await QuasarDiscordChannels.PostAsync(channel, 0, "hi", limiter, default));
        Assert.False(await QuasarDiscordChannels.PostAsync(null, Guild, "hi", limiter, default));
        Assert.False(
            await QuasarDiscordChannels.PostAsync(
                Stub<IDMChannel>((_, _) => 9UL),
                Guild,
                "hi",
                limiter,
                default
            )
        );
        Assert.False(await QuasarDiscordChannels.PostAsync(channel, Guild, "  ", limiter, default));
        Assert.Empty(sent);
        Assert.Equal(2000, QuasarTextSplitter.DiscordLength);
    }

    private static ITextChannel TextChannel(ulong id, ulong guildId, List<Sent> sent) =>
        Stub<ITextChannel>(
            (method, args) =>
                method.Name switch
                {
                    "get_Id" => id,
                    "get_GuildId" => guildId,
                    "SendMessageAsync" => Send(method, args!, sent),
                    _ => throw new NotSupportedException(method.Name),
                }
        );

    private static Task<IUserMessage> Send(MethodInfo method, object?[] args, List<Sent> sent)
    {
        var parameters = method.GetParameters();
        object? Arg(string name) => args[Array.FindIndex(parameters, p => p.Name == name)];
        sent.Add(new Sent((string)Arg("text")!, (AllowedMentions?)Arg("allowedMentions")));
        return Task.FromResult<IUserMessage>(null!);
    }

    private static T Stub<T>(Func<MethodInfo, object?[]?, object?> invoke)
        where T : class
    {
        var stub = DispatchProxy.Create<T, Proxy>();
        ((Proxy)(object)stub).Handler = invoke;
        return stub;
    }

    public class Proxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            Handler(targetMethod!, args);
    }
}
