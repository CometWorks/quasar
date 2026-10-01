using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Magnetar.Protocol.Runtime;
using Quasar.Services.Auth;

namespace Quasar.Services;

/// <summary>Stores one beta notice acknowledgement per signed-in user.</summary>
public sealed class ClusterBetaNoticeCatalog
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _dismissed;

    public ClusterBetaNoticeCatalog(ILogger<ClusterBetaNoticeCatalog> logger)
        : this(Path.Combine(MagnetarPaths.GetQuasarDirectory(), "cluster-beta-dismissals.json"), logger) { }

    internal ClusterBetaNoticeCatalog(string path, ILogger<ClusterBetaNoticeCatalog>? logger = null)
    {
        _path = path;
        try
        {
            _dismissed = File.Exists(path)
                ? JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(path)) ?? []
                : [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            logger?.LogWarning(error, "Could not read cluster beta notice acknowledgements from {Path}.", path);
            _dismissed = [];
        }
    }

    public bool IsDismissed(ClaimsPrincipal user)
    {
        string key = UserKey(user);
        lock (_dismissed) return _dismissed.Contains(key);
    }

    public async Task DismissAsync(ClaimsPrincipal user, CancellationToken token = default)
    {
        string key = UserKey(user);
        await _gate.WaitAsync(token);
        try
        {
            HashSet<string> next;
            lock (_dismissed)
            {
                if (_dismissed.Contains(key)) return;
                next = new(_dismissed, StringComparer.Ordinal) { key };
            }
            await AtomicFileWriter.WriteTextAsync(_path, JsonSerializer.Serialize(next.Order(StringComparer.Ordinal)), token);
            lock (_dismissed) _dismissed.Add(key);
        }
        finally { _gate.Release(); }
    }

    private static string UserKey(ClaimsPrincipal user)
    {
        string? provider = user.FindFirstValue(QuasarClaimTypes.Provider);
        string? subject = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (user.Identity?.IsAuthenticated != true || string.IsNullOrWhiteSpace(provider)
            || string.IsNullOrWhiteSpace(subject))
            throw new UnauthorizedAccessException("A signed-in user is required to dismiss the cluster beta notice.");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(provider + ":" + subject)));
    }
}
