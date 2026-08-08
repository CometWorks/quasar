using Microsoft.AspNetCore.Http;
using Quasar.Services;
using Quasar.Services.Auth;
using System.Text.Json;
using Xunit;

namespace Quasar.Tests;

public sealed class ServicePrincipalAuthenticatorTests
{
    private const string Token = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void AuthenticatesQueryPrincipalWithoutGrantingOtherClusters()
    {
        QuasarAuthOptions options = Options(new ServicePrincipalOptions
        {
            Name = "factory-reader",
            TokenEnvironmentVariable = "FACTORY_READER_TOKEN",
            Scopes = [QuasarScopes.ClusterQuery],
            Clusters = ["production"],
        });
        var authenticator = new ServicePrincipalAuthenticator(options,
            variable => variable == "FACTORY_READER_TOKEN" ? Token : null);
        var context = Context(Token);

        Assert.True(authenticator.TryAuthenticate(context));
        Assert.True(context.User.Identity?.IsAuthenticated);
        Assert.True(context.User.HasClaim(QuasarClaimTypes.Scope, QuasarScopes.ClusterQuery));
        Assert.True(context.User.CanQueryCluster("PRODUCTION"));
        Assert.False(context.User.CanQueryCluster("staging"));
    }

    [Fact]
    public void PreservesClusterManageScope()
    {
        QuasarAuthOptions options = Options(new ServicePrincipalOptions
        {
            Name = "factory-manager",
            TokenEnvironmentVariable = "FACTORY_MANAGER_TOKEN",
            Scopes = [QuasarScopes.ClusterManage],
            Clusters = ["production"],
        });
        var authenticator = new ServicePrincipalAuthenticator(options,
            variable => variable == "FACTORY_MANAGER_TOKEN" ? Token : null);
        var context = Context(Token);

        Assert.True(authenticator.TryAuthenticate(context));
        Assert.True(context.User.HasClaim(QuasarClaimTypes.Scope, QuasarScopes.ClusterManage));
        Assert.True(context.User.CanQueryCluster("production"));
    }

    [Fact]
    public void DuplicateOrWeakConfiguredTokensFailClosed()
    {
        QuasarAuthOptions duplicate = Options(
            Principal("first", "FIRST_TOKEN"),
            Principal("second", "SECOND_TOKEN"));
        var duplicateAuthenticator = new ServicePrincipalAuthenticator(duplicate, _ => Token);
        var duplicateContext = Context(Token);

        Assert.False(duplicateAuthenticator.TryAuthenticate(duplicateContext));
        Assert.False(duplicateContext.User.Identity?.IsAuthenticated ?? false);

        QuasarAuthOptions weak = Options(Principal("weak", "WEAK_TOKEN"));
        var weakAuthenticator = new ServicePrincipalAuthenticator(weak, _ => "too-short");
        Assert.False(weakAuthenticator.TryAuthenticate(Context("too-short")));

        var malformed = new DefaultHttpContext();
        malformed.Request.Headers.Authorization = "Bearer";
        Assert.True(weakAuthenticator.HasBearerAuthorization(malformed.Request));
        Assert.False(weakAuthenticator.TryAuthenticate(malformed));
    }

    [Fact]
    public async Task ClusterAuthorizationFailureIsVersionedJson()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await ClusterApi.WriteAuthorizationErrorAsync(
            context, StatusCodes.Status401Unauthorized, "authentication_required", "Credential required.");

        context.Response.Body.Position = 0;
        using JsonDocument document = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal(1, document.RootElement.GetProperty("protocolVersion").GetInt32());
        Assert.Equal("authentication_required",
            document.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("1", context.Response.Headers["X-Cluster-Gateway-Protocol"]);
    }

    private static DefaultHttpContext Context(string token)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = $"Bearer {token}";
        return context;
    }

    private static ServicePrincipalOptions Principal(string name, string variable) => new()
    {
        Name = name,
        TokenEnvironmentVariable = variable,
        Scopes = [QuasarScopes.ClusterQuery],
        Clusters = ["*"],
    };

    private static QuasarAuthOptions Options(params ServicePrincipalOptions[] principals) => new()
    {
        ServicePrincipals = principals.ToList(),
    };
}
