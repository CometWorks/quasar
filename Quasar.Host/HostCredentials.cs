using System.Text.Json;
using Quasar.ClusterDeployment;

namespace Quasar.Host;

internal static class HostCredentials
{
    internal static void Install(string stateDirectory, global::Quasar.Host.Contract.V1.HostManagedCredentials request)
    {
        if (string.IsNullOrWhiteSpace(request.ClusterId) || !System.Text.RegularExpressions.Regex.IsMatch(request.ClusterId, "^[a-zA-Z0-9_-]{1,64}$")
            || request.ExecutorTokens is null || request.ExecutorTokens.Count is < 1 or > 64
            || request.ExecutorTokens.Keys.Any(k => !System.Text.RegularExpressions.Regex.IsMatch(k, "^[a-zA-Z0-9_-]{1,64}$")))
            throw new InvalidDataException("Invalid cluster credential identity.");
        var all = request.ExecutorTokens.Values.Append(request.AdminToken).Append(request.JoinToken);
        if (all.Any(v => v is null || v.Length != 64 || !v.All(Uri.IsHexDigit))) throw new InvalidDataException("Invalid cluster credential format.");
        string Ref(string purpose) => global::Quasar.Host.Contract.V1.ManagedCredentialReference.Cluster(request.ClusterId, purpose);
        ClusterWorldFiles.Private(stateDirectory);
        string path = Path.Combine(stateDirectory, "credentials.json");
        if (File.Exists(path)) ClusterWorldFiles.Private(path);
        var values = File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllBytes(path))! : [];
        var additions = new Dictionary<string, string> { [Ref("admin")] = request.AdminToken, [Ref("join")] = request.JoinToken };
        foreach (var (host, token) in request.ExecutorTokens) additions[Ref("executor:" + host)] = token;
        foreach (var (name, value) in additions)
            if (values.TryGetValue(name, out var current) && current != value)
                throw new InvalidOperationException("Managed credentials are immutable; use a new cluster identity to replace them.");
        string directory = Path.Combine(stateDirectory, "credentials", request.ClusterId);
        Directory.CreateDirectory(directory); ClusterWorldFiles.Private(directory);
        string tokenFile = Path.Combine(directory, "tokens.json");
        var scoped = new { tokens = request.ExecutorTokens.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new { name = "host-" + p.Key, scope = "Executor",
            sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(p.Value))).ToLowerInvariant() }) };
        if (File.Exists(tokenFile))
        {
            ClusterWorldFiles.Private(tokenFile);
            if (File.ReadAllText(tokenFile) != JsonSerializer.Serialize(scoped))
                throw new InvalidOperationException("The enrolled executor roster is immutable for this cluster identity.");
        }
        else Save(tokenFile, scoped);
        foreach (var pair in additions) values[pair.Key] = pair.Value;
        values[Ref("token-file")] = tokenFile;
        Save(path, values);
        Load(stateDirectory);
    }
    private static void Save<T>(string path, T value)
    {
        string temp = path + "." + Guid.NewGuid().ToString("N");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var file = new FileStream(temp, options))
            { JsonSerializer.Serialize(file, value); file.Flush(true); }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    internal static void Load(string stateDirectory)
    {
        string path = Path.Combine(stateDirectory, "credentials.json");
        if (!File.Exists(path)) return;
        ClusterWorldFiles.Private(path);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var values = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllBytes(path))
            ?? throw new InvalidDataException("Host credentials are empty.");
        foreach (var (key, value) in values)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(key, "^QSR_MANAGED_[A-Z0-9_]{1,120}$") || value is null || value.Length > 65536)
                throw new InvalidDataException("Invalid managed credential.");
            Environment.SetEnvironmentVariable(key, value);
        }
    }
}
