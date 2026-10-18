using System.Globalization;
using System.Net;

namespace Quasar.Bootstrap;

/// <summary>GitHub refused a request because the API rate limit is used up.</summary>
internal sealed class GitHubRateLimitException(HttpStatusCode statusCode, DateTimeOffset? resetAt)
    : HttpRequestException(
        $"GitHub API rate limit exceeded (HTTP {(int)statusCode}).",
        null,
        statusCode
    )
{
    /// <summary>When GitHub accepts requests again, if the response said so.</summary>
    public DateTimeOffset? ResetAt { get; } = resetAt;
}

/// <summary>
/// Timing of the first start's web release download retries. Without a worker there
/// is no web UI, so the launcher keeps trying instead of giving up after one failure.
/// </summary>
internal static class InitialWebReleaseRetry
{
    internal static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan MaximumDelay = TimeSpan.FromMinutes(10);

    // GitHub resets the primary limit within an hour; a later reset means clock skew.
    private static readonly TimeSpan MaximumRateLimitWait = TimeSpan.FromHours(1);
    private static readonly TimeSpan ResetMargin = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Like <see cref="HttpResponseMessage.EnsureSuccessStatusCode"/>, but throws
    /// <see cref="GitHubRateLimitException"/> when GitHub refused the request for its rate limit.
    /// </summary>
    public static void EnsureSuccess(HttpResponseMessage response, DateTimeOffset now)
    {
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
        {
            var remaining = GetHeader(response, "X-RateLimit-Remaining");
            var retryAfter = response.Headers.RetryAfter;
            if (remaining == "0" || retryAfter is not null)
                throw new GitHubRateLimitException(
                    response.StatusCode,
                    GetResetTime(response, now)
                );
        }

        response.EnsureSuccessStatusCode();
    }

    /// <summary>How long to wait before download attempt <paramref name="attempt"/> + 1.</summary>
    public static TimeSpan GetDelay(Exception failure, int attempt, DateTimeOffset now)
    {
        if (failure is GitHubRateLimitException { ResetAt: { } resetAt })
        {
            var untilReset = resetAt - now + ResetMargin;
            if (untilReset < FirstDelay)
                return FirstDelay;
            return untilReset > MaximumRateLimitWait ? MaximumRateLimitWait : untilReset;
        }

        var delay = FirstDelay * Math.Pow(2, Math.Min(attempt - 1, 10));
        return delay > MaximumDelay ? MaximumDelay : delay;
    }

    private static DateTimeOffset? GetResetTime(HttpResponseMessage response, DateTimeOffset now)
    {
        if (
            long.TryParse(
                GetHeader(response, "X-RateLimit-Reset"),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var seconds
            )
            && seconds <= DateTimeOffset.MaxValue.ToUnixTimeSeconds()
        )
            return DateTimeOffset.FromUnixTimeSeconds(seconds);

        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
            return now + delta;
        return retryAfter?.Date;
    }

    private static string? GetHeader(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values)
            ? values.FirstOrDefault()?.Trim()
            : null;
}
