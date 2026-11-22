namespace Quasar.Services.Auth;

public sealed class QuasarTrustedNetworkSettings
{
    public bool RequireHttpsForPublicAccess { get; set; }
    public bool AllowLoopback { get; set; } = true;
    public bool AllowSameSubnet { get; set; } = true;
    public List<string> TrustedProxies { get; set; } = [];
}
