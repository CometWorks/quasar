namespace Quasar.Services.Auth;

public sealed class QuasarTrustedNetworkSettings
{
    public bool AllowLoopback { get; set; } = true;
    // Keep the security dashboard SDK payload compatible with already-loaded clients.
    [System.Text.Json.Serialization.JsonPropertyName("allowSameSubnet")]
    public bool BypassSignInForPrivateLan { get; set; } = true;
    public List<string> TrustedProxies { get; set; } = [];
}
