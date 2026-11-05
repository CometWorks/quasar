using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Quasar.Networking;
using Quasar.Updates;

internal static class TrustedUpdatePackage
{
    // The application account may write staging. Recheck provenance in the
    // updater, then extract a private copy before privileged file replacement.
    internal static async Task<string> PrepareAsync(QuasarServiceInstallation installation, QuasarUpdateOperation operation, CancellationToken token, HttpMessageHandler? handler = null)
    {
        if (!QuasarUpdateJournal.IsVersion(operation.Version)) throw new InvalidDataException("Invalid target release.");
        using var client = handler is null
            ? new HttpClient(new GitHubRetryHandler { InnerHandler = new HttpClientHandler() }) { Timeout = TimeSpan.FromMinutes(10) }
            : new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Quasar.Updater");
        var githubToken = GitHubUpdateTokenReader.ReadToken(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        if (!string.IsNullOrWhiteSpace(githubToken)) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", githubToken);
        var releases = await client.GetFromJsonAsync<JsonElement>($"https://api.github.com/repos/{installation.ReleaseOwner}/{installation.ReleaseRepository}/releases?per_page=100", token);
        var release = releases.EnumerateArray().SingleOrDefault(item => !item.GetProperty("draft").GetBoolean() &&
            Magnetar.Protocol.Runtime.QuasarReleaseVersion.Normalize(item.GetProperty("tag_name").GetString()!) == operation.Version);
        if (release.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException("The target release is not available from the installed trusted repository.");
        var checksumsUrl = release.GetProperty("assets").EnumerateArray().Single(item => item.GetProperty("name").GetString() == "SHA256SUMS").GetProperty("browser_download_url").GetString()!;
        var checksums = await client.GetStringAsync(checksumsUrl, token);
        var expected = checksums.Split('\n').Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length == 2 && parts[1] == installation.PackageAsset).Select(parts => parts[0]).SingleOrDefault();
        if (expected?.Length != 64) throw new InvalidDataException("The trusted release has no package checksum.");
        var stagedArchive = Path.Combine(installation.DataDirectory, "Updates", "Staged", "Package-" + operation.Id.ToString("N"), installation.PackageAsset);
        var privateRoot = Path.Combine(installation.InstallDirectory, ".update-cache", operation.Id.ToString("N"));
        Directory.CreateDirectory(privateRoot);
        var archive = Path.Combine(privateRoot, installation.PackageAsset);
        File.Copy(stagedArchive, archive, overwrite: true);
        await using (var stream = File.OpenRead(archive))
            if (!string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(stream, token)), expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Updater verification of the trusted package checksum failed. No running service was stopped.");
        var extracted = Path.Combine(privateRoot, "extracted");
        if (Directory.Exists(extracted)) Directory.Delete(extracted, recursive: true);
        Directory.CreateDirectory(extracted);
        if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) ZipFile.ExtractToDirectory(archive, extracted);
        else
        {
            await using var file = File.OpenRead(archive); await using var gzip = new GZipStream(file, CompressionMode.Decompress);
            TarFile.ExtractToDirectory(gzip, extracted, overwriteFiles: false);
        }
        var payload = Path.Combine(extracted, "Quasar");
        QuasarApplicationPackage.Validate(payload, operation.Version);
        return payload;
    }
}
