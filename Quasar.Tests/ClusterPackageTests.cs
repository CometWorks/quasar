using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using CometWorks.ClusterGateway.AdminContract.V1;
using Quasar.Services;
using Quasar.Services.Auth;
using Xunit;

namespace Quasar.Tests;

public sealed class ClusterPackageTests
{
    [Fact]
    public void ReleaseNoticeRequiresVersionNewerThanSelectedPin()
    {
        var cluster = new Quasar.Models.ClusterDefinition();
        var release = new ClusterPackageRelease("1.0.4", 1, 2, 3, new string('a', 64));
        Assert.False(ClusterReleaseMonitor.IsUpdateAvailable(cluster, release));
        cluster.PackageSelection = new(1, "1.0.3", new string('b', 64), new string('c', 40), "selection");
        Assert.True(ClusterReleaseMonitor.IsUpdateAvailable(cluster, release));
        Assert.False(ClusterReleaseMonitor.IsUpdateAvailable(cluster, release with { Version = "1.0.3" }));
        Assert.False(ClusterReleaseMonitor.IsUpdateAvailable(cluster, release with { Version = "1.0.2" }));
        cluster.PackageSelection = cluster.PackageSelection with { Version = "1.0.9" };
        Assert.True(ClusterReleaseMonitor.IsUpdateAvailable(cluster, release with { Version = "1.0.10" }));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "private repository", "Updates → GitHub token")]
    [InlineData(HttpStatusCode.Unauthorized, "rejected authentication", "Updates → GitHub token")]
    [InlineData(HttpStatusCode.Forbidden, "denied access", "repository permissions")]
    [InlineData(HttpStatusCode.TooManyRequests, "rate-limited", "Wait")]
    public async Task ReleaseErrorsIdentifyRemoteRepositoryAndRecovery(HttpStatusCode status, string reason, string recovery)
    {
        using var fixture = new Fixture { FailurePath = "/latest", FailureStatus = status };
        var error = await Assert.ThrowsAsync<ClusterPackageException>(() => fixture.Service.GetReleaseAsync(null, default));
        Assert.Contains("fetch the latest stable cluster release from GitHub repository CometWorks/cluster", error.Message);
        Assert.Contains($"HTTP {(int)status}", error.Message);
        Assert.Contains(reason, error.Message);
        Assert.Contains(recovery, error.Message);
        Assert.DoesNotContain("private-repo-token", error.Message);
        Assert.DoesNotContain("untrusted-response-body", error.Message);
    }

    [LinuxTheory]
    [InlineData("/assets/2", "SHA256SUMS for cluster v1.0.3")]
    [InlineData("/assets/1", "ClusterForLinux-1.0.3.tar.gz")]
    public async Task AssetErrorsIdentifyFailedDownloadAndLeaveNoPartialInstallation(string path, string asset)
    {
        using var fixture = new Fixture { FailurePath = path, FailureStatus = HttpStatusCode.ServiceUnavailable };
        var error = await Assert.ThrowsAsync<ClusterPackageException>(() => fixture.Service.StageAsync(fixture.Request, default));
        Assert.Contains("download " + asset + " from GitHub repository CometWorks/cluster (HTTP 503)", error.Message);
        Assert.False(Directory.Exists(fixture.Root) && Directory.EnumerateFileSystemEntries(fixture.Root).Any());
    }

    [Fact]
    public async Task NetworkFailuresHaveContextWhileCallerCancellationRemainsCancellation()
    {
        using var fixture = new Fixture { RequestFailure = new HttpRequestException("sensitive transport detail") };
        var error = await Assert.ThrowsAsync<ClusterPackageException>(() => fixture.Service.GetReleaseAsync(null, default));
        Assert.Contains("CometWorks/cluster", error.Message);
        Assert.Contains("internet connection", error.Message);
        Assert.DoesNotContain("sensitive", error.Message);
        fixture.RequestFailure = new TaskCanceledException();
        error = await Assert.ThrowsAsync<ClusterPackageException>(() => fixture.Service.GetReleaseAsync(null, default));
        Assert.Contains("timed out", error.Message);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.GetReleaseAsync(null, cancellation.Token));
    }

    [Fact]
    public async Task ReleaseApiPreservesActionableRemoteFailure()
    {
        using var fixture = new Fixture { FailurePath = "/latest", FailureStatus = HttpStatusCode.NotFound };
        string catalogPath = Path.Combine(fixture.Root, "catalog");
        Directory.CreateDirectory(Path.Combine(catalogPath, "demo"));
        File.WriteAllText(Path.Combine(catalogPath, "demo", "cluster.json"), """
        { "uniqueName": "demo", "gatewayUrl": "http://gateway.test" }
        """);
        using var catalog = new ClusterCatalog(NullLogger<ClusterCatalog>.Instance,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { ["Quasar:ClusterCatalogPath"] = catalogPath }).Build());
        var result = await ClusterApi.GetPackageRelease("demo", new DefaultHttpContext(), catalog, fixture.Service, default);
        Assert.Equal(502, ((IStatusCodeHttpResult)result).StatusCode);
        var error = Assert.IsType<AdminErrorEnvelope>(((IValueHttpResult)result).Value).Error;
        Assert.Equal("cluster_package_unavailable", error.Code);
        Assert.Contains("CometWorks/cluster (HTTP 404)", error.Message);
        Assert.Contains("Updates → GitHub token", error.Message);
    }

    [LinuxFact]
    public async Task VerifiedReleaseIsPromotedOnceAndModifiedInstallationIsRejected()
    {
        // Optional real release fixture exercises the same consumer without starting any service.
        string? archive = Environment.GetEnvironmentVariable("QUASAR_TEST_CLUSTER_ARCHIVE");
        using var fixture = new Fixture(archive is null ? null : File.ReadAllBytes(archive));
        var results = await Task.WhenAll(fixture.Service.StageAsync(fixture.Request, default),
            fixture.Service.StageAsync(fixture.Request, default));
        Assert.Equal(results[0].PackagePath, results[1].PackagePath);
        Assert.Equal(1, fixture.Downloads);
        Assert.All(ClusterPackageService.RequiredFiles, file => Assert.True(File.Exists(Path.Combine(results[0].PackagePath, file))));
        Assert.True(File.Exists(Path.Combine(fixture.Root, "1.0.3", "installation.json")));
        if (OperatingSystem.IsLinux())
            Assert.NotEqual((UnixFileMode)0, File.GetUnixFileMode(Path.Combine(results[0].PackagePath, "cli/cluster")) & UnixFileMode.UserExecute);
        Assert.Empty(Directory.GetDirectories(fixture.Root, ".stage-*"));
        File.AppendAllText(Path.Combine(results[0].PackagePath, "cli/cluster"), "changed");
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.StageAsync(fixture.Request, default));
    }

    [LinuxFact]
    public async Task SelectedPackageIsVerifiedOfflineAndFailuresNeverReplaceSelection()
    {
        using var fixture = new Fixture();
        var installed = await fixture.Service.StageAsync(fixture.Request, default);
        fixture.Offline = true;
        string catalogPath = Path.Combine(fixture.Root, "catalog");
        Directory.CreateDirectory(Path.Combine(catalogPath, "demo"));
        File.WriteAllText(Path.Combine(catalogPath, "demo", "cluster.json"), """
        { "uniqueName": "demo", "gatewayUrl": "http://gateway.test" }
        """);
        using var catalog = new ClusterCatalog(NullLogger<ClusterCatalog>.Instance,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { ["Quasar:ClusterCatalogPath"] = catalogPath }).Build());
        var store = new ClusterOperationStore(Path.Combine(fixture.Root, "operations"));
        var context = new DefaultHttpContext();
        context.Request.Headers["Idempotency-Key"] = "select-1";
        var request = new ClusterPackageSelectionRequest(fixture.Request.Version, fixture.Request.Sha256, 0);

        var before = Data<ClusterPackageSelectionStatus>(await ClusterApi.GetPackageSelection(
            "demo", context, catalog, fixture.Service, default));
        Assert.Equal(0, before.Revision);
        Assert.False(before.Verified);
        var operation = Data<ClusterOperation>(await ClusterApi.SelectPackage("demo", request,
            context, catalog, fixture.Service, store, default));
        Assert.Equal(ClusterOperationState.Succeeded, operation.State);
        var status = Data<ClusterPackageSelectionStatus>(await ClusterApi.GetPackageSelection(
            "demo", context, catalog, fixture.Service, default));
        Assert.True(status.Verified);
        Assert.Equal(1, status.Revision);

        context.Request.Headers["Idempotency-Key"] = "competing";
        var conflict = Data<ClusterOperation>(await ClusterApi.SelectPackage("demo", request,
            context, catalog, fixture.Service, store, default));
        Assert.Equal(ClusterOperationState.Failed, conflict.State);
        Assert.Equal("package_selection_conflict", conflict.Error!.Code);
        var recovered = new ClusterOperationStore(Path.Combine(fixture.Root, "operations"));
        Assert.Equal(conflict.OperationId, Data<ClusterOperation>(await ClusterApi.SelectPackage("demo", request,
            context, catalog, fixture.Service, recovered, default)).OperationId);

        File.AppendAllText(Path.Combine(installed.PackagePath, "cli/cluster"), "tampered");
        var invalid = Data<ClusterPackageSelectionStatus>(await ClusterApi.GetPackageSelection(
            "demo", context, catalog, fixture.Service, default));
        Assert.False(invalid.Verified);
        Assert.Equal("cluster_package_invalid", invalid.ErrorCode);
        context.Request.Headers["Idempotency-Key"] = "select-tampered";
        var failed = Data<ClusterOperation>(await ClusterApi.SelectPackage("demo", request with { ExpectedRevision = 1 },
            context, catalog, fixture.Service, store, default));
        Assert.Equal(ClusterOperationState.Failed, failed.State);
        Assert.Equal(status.Selection, catalog.GetCluster("demo")!.PackageSelection);

        // Historical operation replay is not proof the files are still valid.
        context.Request.Headers["Idempotency-Key"] = "select-1";
        Assert.Equal(operation.OperationId, Data<ClusterOperation>(await ClusterApi.SelectPackage("demo", request,
            context, catalog, fixture.Service, recovered, default)).OperationId);
        Directory.Delete(Path.GetDirectoryName(installed.PackagePath)!, true);
        Assert.Equal("cluster_package_missing", Data<ClusterPackageSelectionStatus>(await ClusterApi.GetPackageSelection(
            "demo", context, catalog, fixture.Service, default)).ErrorCode);

        Assert.Equal(400, ((IStatusCodeHttpResult)await ClusterApi.SelectPackage("demo", request with { ExpectedRevision = null },
            context, catalog, fixture.Service, store, default)).StatusCode);
        context.User = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(QuasarClaimTypes.Provider, QuasarAuthSchemes.ServicePrincipal),
            new Claim(QuasarClaimTypes.Cluster, "different-cluster")], "test"));
        Assert.Equal(403, ((IStatusCodeHttpResult)await ClusterApi.SelectPackage("demo", request,
            context, catalog, fixture.Service, store, default)).StatusCode);
        Assert.Equal(403, ((IStatusCodeHttpResult)await ClusterApi.GetPackageSelection("demo",
            context, catalog, fixture.Service, default)).StatusCode);
    }

    [LinuxFact]
    public async Task OfflineVerificationRejectsReceiptMismatchAndLinks()
    {
        using var fixture = new Fixture();
        var installed = await fixture.Service.StageAsync(fixture.Request, default);
        fixture.Offline = true;
        Assert.Equal(installed.Commit, (await fixture.Service.GetInstalledAsync(fixture.Request, default)).Commit);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.GetInstalledAsync(
            fixture.Request with { Sha256 = new string('0', 64) }, default));
        string receipt = Path.Combine(fixture.Root, "1.0.3", "installation.json");
        string original = File.ReadAllText(receipt);
        File.WriteAllText(receipt, JsonSerializer.Serialize(installed with { Commit = new string('c', 40) },
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.GetInstalledAsync(fixture.Request, default));
        File.WriteAllText(receipt, original);
        string copy = Path.Combine(fixture.Root, "external-receipt.json");
        File.Move(receipt, copy);
        File.CreateSymbolicLink(receipt, copy);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.GetInstalledAsync(fixture.Request, default));
    }

    private static T Data<T>(IResult result) =>
        Assert.IsType<AdminEnvelope<T>>(((IValueHttpResult)result).Value).Data;

    [LinuxFact]
    public async Task HashMismatchNeverPromotesAndCanBeRetried()
    {
        using var fixture = new Fixture();
        fixture.CorruptDownload = true;
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.StageAsync(fixture.Request, default));
        Assert.Empty(Directory.GetFileSystemEntries(fixture.Root));
        fixture.CorruptDownload = false;
        await fixture.Service.StageAsync(fixture.Request, default);
        Assert.True(Directory.Exists(Path.Combine(fixture.Root, "1.0.3")));
    }

    [LinuxFact]
    public async Task ChangedSelectedChecksumAndPrereleasesAreRejectedBeforeDownload()
    {
        using var fixture = new Fixture();
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.StageAsync(
            fixture.Request with { Sha256 = new string('0', 64) }, default));
        fixture.Prerelease = true;
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.GetReleaseAsync(null, default));
        Assert.Equal(0, fixture.Downloads);
    }

    [LinuxTheory]
    [InlineData("../escape", false)]
    [InlineData("cluster-1.0.3/../escape", false)]
    [InlineData("/absolute/escape", false)]
    [InlineData("cluster-1.0.3/link", true)]
    public async Task UnsafeArchivesNeverPromote(string name, bool link)
    {
        var extra = new PaxTarEntry(link ? TarEntryType.SymbolicLink : TarEntryType.RegularFile, name);
        if (link) extra.LinkName = "/tmp";
        using var fixture = new Fixture(CreateArchive(extra: extra));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.StageAsync(fixture.Request, default));
        Assert.Empty(Directory.GetFileSystemEntries(fixture.Root));
    }

    [LinuxTheory]
    [InlineData("plugins/WorldAuthority/WorldAuthority.dll", false)]
    [InlineData(null, true)]
    public async Task MissingPluginsAndWrongManifestVersionNeverPromote(string? omitted, bool wrongManifest)
    {
        using var fixture = new Fixture(CreateArchive(omitted: omitted, wrongManifest: wrongManifest));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.StageAsync(fixture.Request, default));
        Assert.Empty(Directory.GetFileSystemEntries(fixture.Root));
    }

    [LinuxFact]
    public async Task CancellationDuringDownloadRemovesPartialStaging()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        fixture.BeforeDownload = cancellation.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.StageAsync(fixture.Request, cancellation.Token));
        Assert.Empty(Directory.GetFileSystemEntries(fixture.Root));
    }

    private sealed class LinuxFactAttribute : FactAttribute
    {
        public LinuxFactAttribute() { if (!OperatingSystem.IsLinux()) Skip = "Cluster packages require Linux."; }
    }

    private sealed class LinuxTheoryAttribute : TheoryAttribute
    {
        public LinuxTheoryAttribute() { if (!OperatingSystem.IsLinux()) Skip = "Cluster packages require Linux."; }
    }

    internal static byte[] CreateArchive(TarEntry? extra = null, string? omitted = null, bool wrongManifest = false,
        bool deploymentCapabilities = false)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip))
        {
            foreach (string file in ClusterPackageService.RequiredFiles.Where(file => file != omitted))
            {
                string text = file == "manifest.json" ? JsonSerializer.Serialize(new
                { name = "cluster", version = wrongManifest ? "9.9.9" : "1.0.3", commit = new string('b', 40) }) : "fixture";
                using var data = new MemoryStream(Encoding.UTF8.GetBytes(text));
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "cluster-1.0.3/" + file)
                { DataStream = data, Mode = UnixFileMode.UserRead | UnixFileMode.UserExecute });
            }
            if (extra is not null) tar.WriteEntry(extra);
            if (deploymentCapabilities)
            {
                using var data = new MemoryStream("{\"schemaVersion\":1,\"frozenPluginBundles\":true}"u8.ToArray());
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "cluster-1.0.3/cli/deployment-capabilities.json")
                    { DataStream = data, Mode = UnixFileMode.UserRead });
            }
        }
        return output.ToArray();
    }

    internal sealed class Fixture : HttpMessageHandler, IHttpClientFactory
    {
        private readonly byte[] _archive;
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "quasar-package-" + Guid.NewGuid().ToString("N"));
        public ClusterPackageService Service { get; }
        public ClusterPackageRequest Request { get; }
        public int Downloads;
        public bool CorruptDownload;
        public bool Prerelease;
        public bool Offline;
        public Action? BeforeDownload;
        public string? FailurePath;
        public HttpStatusCode FailureStatus;
        public Exception? RequestFailure;

        public Fixture(byte[]? archive = null)
        {
            _archive = archive ?? CreateArchive();
            Request = new("1.0.3", Convert.ToHexString(SHA256.HashData(_archive)).ToLowerInvariant());
            Service = new(this, () => "private-repo-token", Root);
        }

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (RequestFailure is not null) throw RequestFailure;
            if (Offline) throw new InvalidOperationException("Offline verification must not contact GitHub.");
            Assert.Equal("api.github.com", request.RequestUri!.Host);
            Assert.Equal("private-repo-token", request.Headers.Authorization!.Parameter);
            HttpContent content;
            string path = request.RequestUri.AbsolutePath;
            if (FailurePath is not null && path.EndsWith(FailurePath))
                return Task.FromResult(new HttpResponseMessage(FailureStatus) { Content = new StringContent("untrusted-response-body") });
            if (path.EndsWith("/assets/2"))
            {
                Assert.Contains(request.Headers.Accept, accept => accept.MediaType == "application/octet-stream");
                content = new StringContent(Request.Sha256 + "  ClusterForLinux-1.0.3.tar.gz\n");
            }
            else if (path.EndsWith("/assets/1"))
            {
                Downloads++;
                BeforeDownload?.Invoke();
                cancellationToken.ThrowIfCancellationRequested();
                byte[] bytes = (byte[])_archive.Clone();
                if (CorruptDownload) bytes[0] ^= 1;
                content = new ByteArrayContent(bytes);
            }
            else
            {
                Assert.True(path.EndsWith("/latest") || path.EndsWith("/tags/v1.0.3"));
                content = new StringContent(JsonSerializer.Serialize(new
                {
                    id = 10, tag_name = "v1.0.3", draft = false, prerelease = Prerelease,
                    assets = new[] { new { id = 1, name = "ClusterForLinux-1.0.3.tar.gz", size = _archive.Length },
                        new { id = 2, name = "SHA256SUMS", size = 95 } },
                }));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }

        protected override void Dispose(bool disposing)
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
            base.Dispose(disposing);
        }
    }
}
