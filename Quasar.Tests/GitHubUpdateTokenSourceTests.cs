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
        Assert.Equal(saved is not null, catalog.HasSavedToken);
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

    [Fact]
    public void UpdatesPageSeesTheVariableAndARejectedVariableTokenAsksForANewVariable()
    {
        using var credentials = CreateCatalog("quasar", null);
        using var updater = new QuasarUpdateService(
            new NoNetwork(),
            new QuasarUpdateOptions(),
            new WebServiceOptions { Version = "1.3.0", BootstrapVersion = "1.3.0" },
            credentials,
            NullLogger<QuasarUpdateService>.Instance
        );

        var snapshot = updater.GetSnapshot();
        Assert.True(snapshot.GitHubTokenConfigured);
        Assert.False(snapshot.GitHubTokenSaved);
        Assert.Equal("QUASAR_GITHUB_TOKEN", snapshot.GitHubTokenVariableName);

        typeof(QuasarUpdateService)
            .GetField("_githubTokenRejected", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(updater, true);
        typeof(QuasarUpdateService)
            .GetMethod("SetSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(updater, [snapshot]);

        Assert.EndsWith(
            "Set a new token in QUASAR_GITHUB_TOKEN and restart Quasar.",
            updater.GetSnapshot().GitHubTokenWarning
        );
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

    private sealed class NoNetwork : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            throw new InvalidOperationException("No network expected.");
    }
}
