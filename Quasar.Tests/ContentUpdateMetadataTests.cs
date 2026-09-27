using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Quasar.Services;
using Quasar.Services.Auth;
using Xunit;

namespace Quasar.Tests;

public sealed class ContentUpdateMetadataTests
{
    [Fact]
    public async Task WorkshopUsesTimestampAndRetainsSuccessfulBatchesWhenAnotherFails()
    {
        int calls = 0;
        using var handler = new Handler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("api.steampowered.com", request.RequestUri!.Host);
            calls++;
            if (calls > 1) return new(HttpStatusCode.TooManyRequests);
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
            {
                response = new { publishedfiledetails = new object[] {
                    new { publishedfileid = "123456", result = 1, consumer_app_id = 244850, time_updated = 1700000000, title = "Mod" },
                    new { publishedfileid = "123457", result = 1, consumer_app_id = 123, time_updated = 1700000001, title = "Wrong game" },
                    new { publishedfileid = "123458", result = 1, consumer_app_id = 244850, title = "No timestamp" },
                } }
            })) };
        });
        var resolver = new QuasarWorkshopModResolver(new QuasarAuthOptions(), null!, new Factory(handler),
            NullLogger<QuasarWorkshopModResolver>.Instance);
        var observations = await resolver.CheckUpdatesAsync(Enumerable.Range(123456, 101).Select(i => (long)i).Append(123456));
        Assert.Equal(2, calls);
        Assert.Equal(101, observations.Count);
        Assert.Equal(1700000000, observations[123456].UpdatedTimestamp);
        Assert.Null(observations[123456].Error);
        Assert.NotNull(observations[123457].Error);
        Assert.NotNull(observations[123458].Error);
        Assert.NotNull(observations[123556].Error);
    }

    [Fact]
    public async Task HubReadsManifestCommitAndPersistsConditionalRequestIdentity()
    {
        string root = Path.Combine(Path.GetTempPath(), "hub-content-" + Guid.NewGuid());
        string path = Path.Combine(root, "catalog.json");
        string commit = new('b', 40);
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
        using (var writer = new StreamWriter(zip.CreateEntry("magnetar-hub-main/Plugins/Test.xml").Open()))
            writer.Write($"<PluginData><Id>test</Id><RepoId>CometWorks/test</RepoId><Commit>{commit}</Commit></PluginData>");
        int calls = 0;
        using var handler = new Handler(request =>
        {
            if (++calls > 1)
            {
                Assert.Equal("\"manifest-etag\"", Assert.Single(request.Headers.IfNoneMatch).ToString());
                return new(HttpStatusCode.NotModified);
            }
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes.ToArray()) };
            response.Headers.ETag = new EntityTagHeaderValue("\"manifest-etag\"");
            return response;
        });
        try
        {
            var catalog = new QuasarPluginCatalogService(NullLogger<QuasarPluginCatalogService>.Instance, new Factory(handler), null!, path);
            await catalog.RefreshAsync();
            Assert.Equal(commit, Assert.Single(catalog.GetHubEntries()).SourceCommit);
            var reloaded = new QuasarPluginCatalogService(NullLogger<QuasarPluginCatalogService>.Instance, new Factory(handler), null!, path);
            await reloaded.RefreshAsync();
            Assert.Equal(commit, Assert.Single(reloaded.GetHubEntries()).SourceCommit);
            Assert.Equal(2, calls);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(send(request));
    }
    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
