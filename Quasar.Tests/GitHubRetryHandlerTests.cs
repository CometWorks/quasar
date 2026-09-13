using System.Net;
using Quasar.Networking;
using Xunit;

namespace Quasar.Tests;

public sealed class GitHubRetryHandlerTests
{
    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task RetriesTransientGitHubResponses(HttpStatusCode transientStatus)
    {
        var attempts = 0;
        using var client = CreateClient(_ => Task.FromResult(
            new HttpResponseMessage(++attempts == 1 ? transientStatus : HttpStatusCode.OK)));

        using var response = await client.GetAsync("https://api.github.com/repos/CometWorks/quasar/releases");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task RetriesTransientTransportFailures()
    {
        var attempts = 0;
        using var client = CreateClient(_ =>
        {
            if (++attempts == 1)
                throw new HttpRequestException("Proxy unavailable.");

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        using var response = await client.GetAsync("https://github.com/CometWorks/quasar/archive/main.zip");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, attempts);
    }

    [Theory]
    [InlineData("https://api.github.com/repos/CometWorks/quasar/releases", "POST")]
    [InlineData("https://example.com/update.zip", "GET")]
    public async Task DoesNotRetryUnsafeOrNonGitHubRequests(string url, string method)
    {
        var attempts = 0;
        using var client = CreateClient(_ => Task.FromResult(
            new HttpResponseMessage(++attempts > 0 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)));
        using var request = new HttpRequestMessage(new HttpMethod(method), url);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task StopsAfterConfiguredRetryLimit()
    {
        var attempts = 0;
        var retryLogs = new List<string>();
        using var client = CreateClient(
            _ =>
            {
                attempts++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway));
            },
            maxRetries: 2,
            onRetry: (message, _) => retryLogs.Add(message));

        using var response = await client.GetAsync("https://objects.githubusercontent.com/update.zip");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(3, attempts);
        Assert.Equal(2, retryLogs.Count);
    }

    private static HttpClient CreateClient(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> respond,
        int maxRetries = 1,
        Action<string, Exception?>? onRetry = null)
    {
        var retryHandler = new GitHubRetryHandler(
            onRetry,
            maxRetries,
            initialDelay: TimeSpan.Zero,
            maximumDelay: TimeSpan.Zero)
        {
            InnerHandler = new StubHandler(respond),
        };
        return new HttpClient(retryHandler);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => respond(request);
    }
}
