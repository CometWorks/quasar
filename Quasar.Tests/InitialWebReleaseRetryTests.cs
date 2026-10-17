extern alias Bootstrap;

using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bootstrap::Quasar.Bootstrap;
using Magnetar.Protocol.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Quasar.Tests;

[Collection("Exact server creation")]
public sealed class InitialWebReleaseRetryTests
{
    private const string ReleasesUrl =
        "https://api.github.com/repos/CometWorks/quasar/releases?per_page=100";

    [Fact]
    public async Task FirstStartWaitsForTheRateLimitResetAndThenDownloadsTheWorker()
    {
        await WithRootAsync(async () =>
        {
            var reset = DateTimeOffset.UtcNow.AddMinutes(20);
            var github = new FakeGitHub(CreateWebArchive());
            github.RateLimitedReleaseRequests = 2;
            github.ResetAt = reset;
            var delays = new List<TimeSpan>();
            using var coordinator = CreateCoordinator(github, delays);

            await coordinator.WaitForInitialWebReleaseAsync(CancellationToken.None);

            Assert.Equal(3, github.ReleaseRequests);
            Assert.Equal(2, delays.Count);
            // Waits until GitHub resets the limit, not a fixed short backoff.
            Assert.All(
                delays,
                delay => Assert.InRange(delay, TimeSpan.FromMinutes(19), TimeSpan.FromMinutes(21))
            );
            var pointer = JsonSerializer.Deserialize<QuasarActiveReleasePointer>(
                File.ReadAllText(MagnetarPaths.GetQuasarActiveReleasePath()),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)
            )!;
            Assert.Equal("1.4.0", pointer.Version);
            Assert.True(File.Exists(pointer.FileName));
        });
    }

    [Fact]
    public async Task FailedDownloadUsesALocalWorkerInsteadOfWaiting()
    {
        await WithRootAsync(async () =>
        {
            var github = new FakeGitHub(CreateWebArchive())
            {
                RateLimitedReleaseRequests = int.MaxValue,
            };
            var delays = new List<TimeSpan>();
            using var coordinator = CreateCoordinator(github, delays, hasFallbackWorker: true);

            await coordinator.WaitForInitialWebReleaseAsync(CancellationToken.None);

            Assert.Equal(1, github.ReleaseRequests);
            Assert.Empty(delays);
        });
    }

    [Fact]
    public void RecognizesGitHubRateLimitResponses()
    {
        var now = DateTimeOffset.UtcNow;
        var reset = DateTimeOffset.FromUnixTimeSeconds(now.AddMinutes(7).ToUnixTimeSeconds());
        using var limited = RateLimited(reset);
        var exception = Assert.Throws<GitHubRateLimitException>(() =>
            InitialWebReleaseRetry.EnsureSuccess(limited, now)
        );
        Assert.Equal(reset, exception.ResetAt);

        using var secondary = new HttpResponseMessage(HttpStatusCode.Forbidden);
        secondary.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
            TimeSpan.FromSeconds(90)
        );
        Assert.Equal(
            now.AddSeconds(90),
            Assert
                .Throws<GitHubRateLimitException>(() =>
                    InitialWebReleaseRetry.EnsureSuccess(secondary, now)
                )
                .ResetAt
        );

        // A plain 403 (no access, bad token) is not a rate limit.
        using var forbidden = new HttpResponseMessage(HttpStatusCode.Forbidden);
        var other = Assert.Throws<HttpRequestException>(() =>
            InitialWebReleaseRetry.EnsureSuccess(forbidden, now)
        );
        Assert.IsNotType<GitHubRateLimitException>(other);
    }

    [Fact]
    public void BacksOffOtherFailuresAndWaitsForTheReset()
    {
        var now = DateTimeOffset.UtcNow;
        var network = new HttpRequestException("Name or service not known");
        Assert.Equal(TimeSpan.FromSeconds(30), InitialWebReleaseRetry.GetDelay(network, 1, now));
        Assert.Equal(TimeSpan.FromSeconds(60), InitialWebReleaseRetry.GetDelay(network, 2, now));
        Assert.Equal(TimeSpan.FromSeconds(240), InitialWebReleaseRetry.GetDelay(network, 4, now));
        Assert.Equal(TimeSpan.FromMinutes(10), InitialWebReleaseRetry.GetDelay(network, 50, now));

        var limit = (DateTimeOffset? resetAt) =>
            new GitHubRateLimitException(HttpStatusCode.Forbidden, resetAt);
        Assert.Equal(
            TimeSpan.FromSeconds(605),
            InitialWebReleaseRetry.GetDelay(limit(now.AddMinutes(10)), 1, now)
        );
        Assert.Equal(
            TimeSpan.FromSeconds(30),
            InitialWebReleaseRetry.GetDelay(limit(now.AddMinutes(-1)), 1, now)
        );
        Assert.Equal(
            TimeSpan.FromHours(1),
            InitialWebReleaseRetry.GetDelay(limit(now.AddDays(1)), 1, now)
        );
        Assert.Equal(
            TimeSpan.FromSeconds(60),
            InitialWebReleaseRetry.GetDelay(limit(null), 2, now)
        );
    }

    private static LauncherCoordinator CreateCoordinator(
        FakeGitHub github,
        List<TimeSpan> delays,
        bool hasFallbackWorker = false
    ) =>
        new(
            new BootstrapOptions(),
            new LauncherForegroundOptions(true),
            NullLogger<LauncherCoordinator>.Instance,
            github
        )
        {
            Delay = (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            },
            HasFallbackWorker = () => hasFallbackWorker,
        };

    private static HttpResponseMessage RateLimited(DateTimeOffset reset)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            ReasonPhrase = "rate limit exceeded",
        };
        response.Headers.Add("X-RateLimit-Limit", "60");
        response.Headers.Add("X-RateLimit-Remaining", "0");
        response.Headers.Add("X-RateLimit-Reset", reset.ToUnixTimeSeconds().ToString());
        return response;
    }

    private static byte[] CreateWebArchive()
    {
        var source = Path.Combine(MagnetarPaths.GetQuasarDirectory(), "web-source");
        foreach (
            var relativePath in QuasarWebReleaseLayout.RequiredWebAssetFiles.Append(
                QuasarWebReleaseLayout.WorkerExecutableFileName
            )
        )
        {
            var path = Path.Combine(source, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, relativePath);
        }

        using var archive = new MemoryStream();
        if (
            new BootstrapOptions().WebAssetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
        )
        {
            ZipFile.CreateFromDirectory(source, archive);
            return archive.ToArray();
        }

        using (var gzip = new GZipStream(archive, CompressionLevel.Fastest, leaveOpen: true))
            TarFile.CreateFromDirectory(source, gzip, includeBaseDirectory: false);
        return archive.ToArray();
    }

    private static async Task WithRootAsync(Func<Task> action)
    {
        var cache = typeof(MagnetarPaths).GetField(
            "_cachedQuasarDirectory",
            BindingFlags.Static | BindingFlags.NonPublic
        )!;
        var previous = cache.GetValue(null);
        var directory = Path.Combine(
            Path.GetTempPath(),
            "quasar-initial-download-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(directory);
        cache.SetValue(null, directory);
        try
        {
            await action();
        }
        finally
        {
            cache.SetValue(null, previous);
            Directory.Delete(directory, true);
        }
    }

    // Serves the release list, SHA256SUMS and the web archive the way GitHub does.
    private sealed class FakeGitHub(byte[] webArchive) : HttpMessageHandler
    {
        private const string AssetBase =
            "https://github.com/CometWorks/quasar/releases/download/v1.4.0/";
        private static readonly string WebAssetName = new BootstrapOptions().WebAssetName;

        public int RateLimitedReleaseRequests { get; set; }

        public DateTimeOffset ResetAt { get; set; } = DateTimeOffset.UtcNow;

        public int ReleaseRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url == ReleasesUrl)
            {
                if (++ReleaseRequests <= RateLimitedReleaseRequests)
                    return Task.FromResult(RateLimited(ResetAt));

                var releases = $$"""
                    [{"tag_name":"v1.4.0","draft":false,"prerelease":false,"assets":[
                      {"name":"{{WebAssetName}}","browser_download_url":"{{AssetBase}}{{WebAssetName}}"},
                      {"name":"SHA256SUMS","browser_download_url":"{{AssetBase}}SHA256SUMS"}]}]
                    """;
                return Ok(new StringContent(releases, Encoding.UTF8, "application/json"));
            }

            if (url == AssetBase + "SHA256SUMS")
                return Ok(
                    new StringContent(
                        $"{Convert.ToHexString(SHA256.HashData(webArchive)).ToLowerInvariant()}  {WebAssetName}\n"
                    )
                );

            if (url == AssetBase + WebAssetName)
                return Ok(new ByteArrayContent(webArchive));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static Task<HttpResponseMessage> Ok(HttpContent content) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    }
}
