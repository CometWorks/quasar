using System.Net;
using System.Reflection;
using Magnetar.Protocol.Runtime;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Quasar.Services;
using Quasar.Services.Updates;
using Xunit;

namespace Quasar.Tests;

[Collection("Exact server creation")]
public sealed class GitHubUpdateTokenSourceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "quasar-github-token-" + Guid.NewGuid().ToString("N")
    );
    private readonly FieldInfo _cache = typeof(MagnetarPaths).GetField(
        "_cachedQuasarDirectory",
        BindingFlags.NonPublic | BindingFlags.Static
    )!;
    private readonly object? _previousRoot;
    private readonly EphemeralDataProtectionProvider _protection = new();

    public GitHubUpdateTokenSourceTests()
    {
        _previousRoot = _cache.GetValue(null);
        Directory.CreateDirectory(_root);
        _cache.SetValue(null, _root);
    }

    [Theory]
    [InlineData(null, null, null, "", "")]
    [InlineData(null, null, " generic ", "generic", "GITHUB_TOKEN")]
    [InlineData(null, "saved", "generic", "saved", "")]
    [InlineData(" quasar ", "saved", "generic", "quasar", "QUASAR_GITHUB_TOKEN")]
    [InlineData(" ", null, "generic", "generic", "GITHUB_TOKEN")]
    [InlineData(null, " ", " generic ", "generic", "GITHUB_TOKEN")]
    public async Task TokenFollowsTheLauncherOrder(
        string? quasarVariable,
        string? saved,
        string? genericVariable,
        string expectedToken,
        string expectedVariable
    )
    {
        if (saved is not null)
        {
            using var writer = CreateCatalog(null, null);
            await writer.SaveAsync(new GitHubUpdateCredentials { Token = saved });
        }

        using var catalog = CreateCatalog(quasarVariable, genericVariable);

        Assert.Equal(expectedToken, catalog.Token);
        Assert.Equal(expectedToken.Length > 0, catalog.HasToken);
        Assert.Equal(expectedVariable, catalog.TokenVariableName);
        Assert.Equal(!string.IsNullOrWhiteSpace(saved), catalog.HasSavedToken);
    }

    [Fact]
    public async Task SavingATokenReplacesGitHubTokenButNotQuasarGitHubToken()
    {
        using var generic = CreateCatalog(null, "generic");
        using var quasar = CreateCatalog("quasar", null);

        await generic.SaveAsync(new GitHubUpdateCredentials { Token = "saved" });
        await quasar.SaveAsync(new GitHubUpdateCredentials { Token = "saved" });

        Assert.Equal(("saved", ""), (generic.Token, generic.TokenVariableName));
        Assert.Equal(("quasar", "QUASAR_GITHUB_TOKEN"), (quasar.Token, quasar.TokenVariableName));

        await quasar.ClearAsync();
        Assert.Equal("quasar", quasar.Token);
        Assert.False(quasar.HasSavedToken);
    }

    [Theory]
    [InlineData("quasar", null, "QUASAR_GITHUB_TOKEN", "quasar")]
    [InlineData(null, "generic", "GITHUB_TOKEN", "generic")]
    public async Task UpdatesPageSeesTheVariableAndARejectedVariableTokenAsksForANewVariable(
        string? quasarVariable, string? genericVariable, string expectedVariable, string expectedToken)
    {
        using var credentials = CreateCatalog(quasarVariable, genericVariable);
        using var handler = new RejectedTokenHandler(expectedToken);
        using var updater = new QuasarUpdateService(
            new Factory(handler),
            new QuasarUpdateOptions(),
            new WebServiceOptions { Version = "1.3.0", BootstrapVersion = "1.3.0" },
            credentials,
            NullLogger<QuasarUpdateService>.Instance
        );

        var snapshot = updater.GetSnapshot();
        Assert.True(snapshot.GitHubTokenConfigured);
        Assert.False(snapshot.GitHubTokenSaved);
        Assert.Equal(expectedVariable, snapshot.GitHubTokenVariableName);

        await updater.CheckNowAsync();

        Assert.Equal(1, handler.Requests);
        Assert.True(updater.GetSnapshot().GitHubTokenExpired);
        Assert.EndsWith(
            $"Set a new token in {expectedVariable} and restart Quasar.",
            updater.GetSnapshot().GitHubTokenWarning
        );

        await credentials.SaveAsync(new GitHubUpdateCredentials { Token = "saved" });
        Assert.True(updater.GetSnapshot().GitHubTokenSaved);
        Assert.Equal(quasarVariable is not null, updater.GetSnapshot().GitHubTokenExpired);

        await credentials.ClearAsync();
        var cleared = updater.GetSnapshot();
        Assert.False(cleared.GitHubTokenSaved);
        Assert.True(cleared.GitHubTokenConfigured);
        Assert.Equal(expectedVariable, cleared.GitHubTokenVariableName);
        Assert.Equal(quasarVariable is not null, cleared.GitHubTokenExpired);
        Assert.Equal("GitHub update token cleared.", cleared.Message);
    }

    public void Dispose()
    {
        _cache.SetValue(null, _previousRoot);
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException) { }
    }

    private GitHubUpdateCredentialsCatalog CreateCatalog(
        string? quasarVariable,
        string? genericVariable
    ) =>
        new(
            NullLogger<GitHubUpdateCredentialsCatalog>.Instance,
            _protection,
            name =>
                name switch
                {
                    GitHubUpdateCredentialsCatalog.QuasarTokenVariableName => quasarVariable,
                    GitHubUpdateCredentialsCatalog.GenericTokenVariableName => genericVariable,
                    _ => null,
                }
        );

    private sealed class RejectedTokenHandler(string expectedToken) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal(expectedToken, request.Headers.Authorization?.Parameter);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
