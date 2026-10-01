using System.Security.Claims;
using Quasar.Services;
using Quasar.Services.Auth;
using Xunit;

namespace Quasar.Tests;

public sealed class ClusterBetaNoticeCatalogTests
{
    [Fact]
    public async Task DismissalPersistsPerUserAcrossCatalogInstances()
    {
        string directory = Path.Combine(Path.GetTempPath(), "cluster-beta-notice-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "dismissals.json");
        static ClaimsPrincipal User(string id) => new(new ClaimsIdentity(
            [new(QuasarClaimTypes.Provider, QuasarAuthSchemes.Steam), new(ClaimTypes.NameIdentifier, id)],
            QuasarAuthSchemes.Steam));
        try
        {
            var first = new ClusterBetaNoticeCatalog(path);
            Assert.False(first.IsDismissed(User("one")));
            await first.DismissAsync(User("one"));
            await first.DismissAsync(User("one"));

            var reloaded = new ClusterBetaNoticeCatalog(path);
            Assert.True(reloaded.IsDismissed(User("one")));
            Assert.False(reloaded.IsDismissed(User("two")));
            Assert.DoesNotContain("one", await File.ReadAllTextAsync(path));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
