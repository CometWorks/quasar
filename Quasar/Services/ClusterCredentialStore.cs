using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Magnetar.Protocol.Runtime;
using Microsoft.AspNetCore.DataProtection;

namespace Quasar.Services;

public sealed class ClusterCredentialStore
{
    private readonly IDataProtector _protector;
    private readonly string _path;
    private readonly object _sync = new();
    private readonly Dictionary<string, string> _values;
    public ClusterCredentialStore(IDataProtectionProvider protection, ILogger<ClusterCredentialStore> logger)
        : this(protection, Path.Combine(MagnetarPaths.GetQuasarDirectory(), "cluster-credentials.json"), logger) { }
    internal ClusterCredentialStore(IDataProtectionProvider protection, string path, ILogger<ClusterCredentialStore>? logger = null)
    {
        _protector = protection.CreateProtector("Quasar.ClusterCredentials.v1");
        _path = path;
        _values = Load(path, logger);
    }
    // This singleton is a dependency of most cluster services; a torn file must not keep Quasar from starting.
    private static Dictionary<string, string> Load(string path, ILogger? logger)
    {
        if (!File.Exists(path)) return [];
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllBytes(path))
                ?? throw new InvalidDataException("The credential file is empty.");
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            // Set aside, never overwritten: the next credential write would otherwise destroy what may be recoverable.
            string quarantine = path + ".corrupt-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss");
            File.Move(path, quarantine, overwrite: true);
            logger?.LogError(exception, "Cluster credential file {Path} is unreadable and was moved to {Quarantine}. Managed cluster and Host credentials must be restored from it or re-created.", path, quarantine);
            return [];
        }
    }
    public static string Reference(string owner, string purpose) => "QSR_MANAGED_" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(owner + ":" + purpose)));
    internal string Create(string owner, string purpose)
    {
        string reference = Reference(owner, purpose);
        lock (_sync)
        {
            if (!_values.ContainsKey(reference))
            {
                var next = new Dictionary<string, string>(_values) { [reference] = _protector.Protect(Convert.ToHexString(RandomNumberGenerator.GetBytes(32))) };
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                string temporary = _path + "." + Guid.NewGuid().ToString("N");
                try
                {
                    var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
                    if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                    using (var file = new FileStream(temporary, options))
                    { JsonSerializer.Serialize(file, next); file.Flush(true); }
                    File.Move(temporary, _path, true);
                    _values[reference] = next[reference];
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
        }
        return reference;
    }
    public string? Resolve(string reference)
    {
        lock (_sync) return _values.TryGetValue(reference, out var value) ? _protector.Unprotect(value) : Environment.GetEnvironmentVariable(reference);
    }
}
