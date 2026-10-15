using System.Security.Cryptography;
using System.Text;
using Magnetar.Protocol.Model;
using Quasar.Host.Contract.V1;

namespace Quasar.Services;

/// <summary>
/// Checks the token an agent presents on /ws/agent against the identity its Hello claims.
/// A standalone server's token is derived from a key only Quasar holds and the server's unique name,
/// and handed to the server in its launch environment. A cluster node's token comes from the
/// enrollment credential of the Host it claims to run on.
/// </summary>
public sealed class AgentSocketAuthenticator(
    ClusterCredentialStore credentials,
    ClusterHostCatalog hosts
)
{
    private readonly Lazy<string> _key = new(
        () => credentials.Resolve(credentials.Create("quasar", "agent-socket"))!,
        LazyThreadSafetyMode.PublicationOnly
    );

    public string ForServer(string uniqueName) =>
        AgentSocketToken.Derive(_key.Value, "server:" + uniqueName);

    public bool IsValid(AgentHello hello, string token)
    {
        string? expected = hello.ClusterMode
            ? hosts.Get(hello.HostId) is { } host
            && credentials.Resolve(host.CredentialReference) is { } credential
                ? AgentSocketToken.ForHost(credential)
                : null
            : string.IsNullOrEmpty(hello.UniqueName)
                ? null
                : ForServer(hello.UniqueName);
        return expected is not null
            && CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(expected),
                Encoding.UTF8.GetBytes(token)
            );
    }
}
