using System.Net;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Quasar.Services;
using Quasar.Services.Auth;
using Xunit;

namespace Quasar.Tests;

// Unlisted, friends-only and private Workshop items answer result 9 to the keyless API (#1199).
public sealed class HiddenWorkshopItemTests
{
    private const long Hidden = 3816175430, Public = 2344068716;

    [Fact]
    public async Task KeyedDetailsPinHiddenItems()
    {
        var keyed = new List<string>();
        var resolver = await ResolverAsync(request =>
        {
            if (request.Method == HttpMethod.Post) return Json(KeylessResponse);
            Assert.Equal("/IPublishedFileService/GetDetails/v1/", request.RequestUri!.AbsolutePath);
            keyed.Add(request.RequestUri.Query);
            // Shape of a GetDetails answer for an item the key's account can see.
            return Json("""
                {"response":{"publishedfiledetails":[{"result":1,"publishedfileid":"3816175430","creator_appid":244850,
                "consumer_appid":244850,"file_size":"28247729","title":"Trade Lanes MP","time_created":1790000000,
                "time_updated":1792205139,"visibility":2,"file_type":0,"tags":[{"tag":"Mod"}]}]}}
                """);
        });

        var observations = await resolver.CheckUpdatesAsync([Hidden, Public]);

        string query = Assert.Single(keyed);
        Assert.Contains("publishedfileids%5B0%5D=3816175430", query);
        Assert.DoesNotContain("2344068716", query);
        Assert.Equal(new WorkshopUpdateObservation(Hidden, "Trade Lanes MP", 1792205139, null), observations[Hidden]);
        Assert.Equal(1785868586, observations[Public].UpdatedTimestamp);
        Assert.Equal(1792205139, ClusterWorkshopModStager.PinnedVersion(Hidden, observations));
    }

    [Fact]
    public async Task ItemsNoKeyCanSeeAreLeftToSteamCmd()
    {
        var resolver = await ResolverAsync(request => request.Method == HttpMethod.Post
            ? Json(KeylessResponse)
            : Json("""{"response":{"publishedfiledetails":[{"result":9,"publishedfileid":"3816175430"}]}}"""));

        var observations = await resolver.CheckUpdatesAsync([Hidden, Public]);

        Assert.True(observations[Hidden].Hidden);
        Assert.Null(observations[Hidden].UpdatedTimestamp);
        Assert.Contains("unlisted", observations[Hidden].Error);
        Assert.Null(ClusterWorkshopModStager.PinnedVersion(Hidden, observations));
        Assert.False(observations[Public].Hidden);
        Assert.Equal(1785868586, ClusterWorkshopModStager.PinnedVersion(Public, observations));
        // Everything that needs an observed version still refuses the hidden item.
        Assert.Throws<InvalidOperationException>(() => ClusterWorkshopModStager.ObservedVersion(Hidden, observations));
    }

    [Fact]
    public async Task KeyedFailureKeepsKeylessObservations()
    {
        var resolver = await ResolverAsync(request => request.Method == HttpMethod.Post
            ? Json(KeylessResponse)
            : new(HttpStatusCode.Forbidden));

        var observations = await resolver.CheckUpdatesAsync([Hidden, Public]);

        Assert.True(observations[Hidden].Hidden);
        Assert.Equal(1785868586, observations[Public].UpdatedTimestamp);
    }

    [Fact]
    public void UnavailableItemsThatAreNotHiddenStillFail()
    {
        var observations = new Dictionary<long, WorkshopUpdateObservation> { [123] = new(123, "mod", null, "check failed") };
        Assert.Throws<InvalidOperationException>(() => ClusterWorkshopModStager.PinnedVersion(123, observations));
        Assert.Throws<InvalidOperationException>(() => ClusterWorkshopModStager.PinnedVersion(456, observations));
    }

    [Fact]
    public void DownloadedVersionReadsSteamCmdWorkshopManifest()
    {
        // appworkshop_244850.acf as SteamCMD wrote it after downloading both items anonymously, trimmed.
        const string acf = """
            "AppWorkshop"
            {
            	"appid"		"244850"
            	"WorkshopItemsInstalled"
            	{
            		"2344068716"
            		{
            			"size"		"1205201482"
            			"timeupdated"		"1785868586"
            			"manifest"		"4283447432016624117"
            		}
            		"3816175430"
            		{
            			"size"		"28247729"
            			"timeupdated"		"1792205139"
            			"manifest"		"5321933892077145359"
            		}
            	}
            	"WorkshopItemDetails"
            	{
            		"3816175430"
            		{
            			"manifest"		"5321933892077145359"
            			"timeupdated"		"1792205139"
            			"timetouched"		"1792498589"
            		}
            	}
            }
            """;
        Assert.Equal(1792205139, ClusterWorkshopModStager.DownloadedVersion(acf, "3816175430"));
        Assert.Equal(1785868586, ClusterWorkshopModStager.DownloadedVersion(acf, "2344068716"));
        Assert.Null(ClusterWorkshopModStager.DownloadedVersion(acf, "3817490449"));
        Assert.Null(ClusterWorkshopModStager.DownloadedVersion("\"AppWorkshop\" { }", "3816175430"));
    }

    // The keyless answer for one hidden and one public item, trimmed.
    private const string KeylessResponse = """
        {"response":{"result":1,"resultcount":2,"publishedfiledetails":[{"publishedfileid":"3816175430","result":9},
        {"publishedfileid":"2344068716","result":1,"consumer_app_id":244850,"title":"Industrial Overhaul - v1.7.7",
        "time_updated":1785868586,"visibility":0,"tags":[{"tag":"Mod"}]}]}}
        """;

    private static async Task<QuasarWorkshopModResolver> ResolverAsync(Func<HttpRequestMessage, HttpResponseMessage> send)
    {
        var credentials = new SteamWorkshopCredentialsCatalog(NullLogger<SteamWorkshopCredentialsCatalog>.Instance,
            new EphemeralDataProtectionProvider());
        await credentials.SaveAsync(new() { WebApiKey = "test-web-api-key" });
        return new(new QuasarAuthOptions(), credentials, new Factory(new Handler(send)), NullLogger<QuasarWorkshopModResolver>.Instance);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(send(request));
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
