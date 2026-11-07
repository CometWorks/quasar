using Magnetar.Protocol.Bridge;
using Magnetar.Protocol.Model;
using Quasar.Agent;
using Xunit;

namespace Quasar.Tests;

public sealed class CompanionHandlerTests
{
    private const string Id = "9AC2F6A8-B8A7-4250-8814-BADE62C5CF3C";

    public sealed class DuckPlugin
    {
        public string QuasarCompanionPluginId => Id;

        public Task<string> HandleQuasarCompanionRequestAsync(
            string operation,
            string payloadJson,
            CancellationToken cancellationToken
        ) =>
            operation switch
            {
                "echo" => Task.FromResult("{\"echo\":" + payloadJson + "}"),
                "throw" => throw new InvalidOperationException("The backup folder is missing."),
                "fault" => Task.FromException<string>(
                    new AggregateException(new TimeoutException("Took too long."))
                ),
                _ => Task.FromResult<string>(null!),
            };
    }

    public sealed class WrongReturnType
    {
        public string QuasarCompanionPluginId => Id;

        public Task<object> HandleQuasarCompanionRequestAsync(
            string operation,
            string payloadJson,
            CancellationToken cancellationToken
        ) => Task.FromResult<object>("{}");
    }

    public sealed class WrongParameters
    {
        public string QuasarCompanionPluginId => Id;

        public Task<string> HandleQuasarCompanionRequestAsync(
            string operation,
            string payloadJson
        ) => Task.FromResult("{}");
    }

    public sealed class NoPluginId
    {
        public Task<string> HandleQuasarCompanionRequestAsync(
            string operation,
            string payloadJson,
            CancellationToken cancellationToken
        ) => Task.FromResult("{}");
    }

    public sealed class Both : IQuasarCompanionRequestHandler
    {
        public string PluginId => Id;

        public string QuasarCompanionPluginId => Id;

        public Task<CompanionPluginResponse> HandleQuasarCompanionRequestAsync(
            CompanionPluginRequest request,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                new CompanionPluginResponse { Success = true, PayloadJson = "interface" }
            );

        public Task<string> HandleQuasarCompanionRequestAsync(
            string operation,
            string payloadJson,
            CancellationToken cancellationToken
        ) => Task.FromResult("duck");
    }

    private static Task<CompanionPluginResponse> Ask(
        object plugin,
        string operation,
        string payload = "1"
    ) =>
        CompanionHandlers
            .Find(plugin, "loaded-id", Id.ToLowerInvariant())!
            .HandleQuasarCompanionRequestAsync(
                new CompanionPluginRequest
                {
                    PluginId = Id,
                    Operation = operation,
                    PayloadJson = payload,
                },
                default
            );

    [Fact]
    public async Task ADuckTypedPluginAnswersAndItsFailuresBecomeErrors()
    {
        var plugin = new DuckPlugin();
        Assert.Null(CompanionHandlers.Find(plugin, "loaded-id", "another-plugin"));
        Assert.Equal(Id, CompanionHandlers.Find(plugin, "loaded-id", Id)!.PluginId);

        var answer = await Ask(plugin, "echo", "[2]");
        Assert.True(answer.Success);
        Assert.Equal("{\"echo\":[2]}", answer.PayloadJson);

        var thrown = await Ask(plugin, "throw");
        Assert.False(thrown.Success);
        Assert.Equal("The backup folder is missing.", thrown.Error);

        var faulted = await Ask(plugin, "fault");
        Assert.False(faulted.Success);
        Assert.Equal("Took too long.", faulted.Error);

        var empty = await Ask(plugin, "null");
        Assert.False(empty.Success);
        Assert.Equal("Companion plugin returned no response.", empty.Error);
    }

    [Fact]
    public async Task WrongSignaturesAreIgnoredAndTheInterfaceWins()
    {
        Assert.Null(CompanionHandlers.Find(new WrongReturnType(), Id, Id));
        Assert.Null(CompanionHandlers.Find(new WrongParameters(), Id, Id));
        Assert.Null(CompanionHandlers.Find(new NoPluginId(), Id, Id));
        Assert.Null(CompanionHandlers.Find(new object(), Id, Id));

        Assert.Equal("interface", (await Ask(new Both(), "echo")).PayloadJson);
    }
}
