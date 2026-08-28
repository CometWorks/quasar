using System.Net;

namespace Quasar.Networking;

/// <summary>Retries transient idempotent requests to GitHub hosts.</summary>
public sealed class GitHubRetryHandler : DelegatingHandler
{
    public const int DefaultMaxRetries = 4;

    private static readonly TimeSpan DefaultInitialDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DefaultMaximumDelay = TimeSpan.FromSeconds(30);

    private readonly Action<string, Exception?>? _onRetry;
    private readonly int _maxRetries;
    private readonly TimeSpan _initialDelay;
    private readonly TimeSpan _maximumDelay;

    public GitHubRetryHandler(
        Action<string, Exception?>? onRetry = null,
        int maxRetries = DefaultMaxRetries,
        TimeSpan? initialDelay = null,
        TimeSpan? maximumDelay = null)
    {
        if (maxRetries is < 0 or > 10)
            throw new ArgumentOutOfRangeException(nameof(maxRetries));

        _initialDelay = initialDelay ?? DefaultInitialDelay;
        _maximumDelay = maximumDelay ?? DefaultMaximumDelay;
        if (_initialDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(initialDelay));
        if (_maximumDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maximumDelay));

        _onRetry = onRetry;
        _maxRetries = maxRetries;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (!ShouldRetry(request))
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        for (var retryNumber = 1; ; retryNumber++)
        {
            try
            {
                var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (retryNumber > _maxRetries || !IsTransient(response.StatusCode))
                    return response;

                var delay = GetDelay(response, retryNumber);
                NotifyRetry(request, retryNumber, delay, $"HTTP {(int)response.StatusCode}", null);
                response.Dispose();
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                retryNumber <= _maxRetries &&
                IsTransient(exception, cancellationToken))
            {
                var delay = GetDelay(response: null, retryNumber);
                NotifyRetry(request, retryNumber, delay, exception.Message, exception);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool ShouldRetry(HttpRequestMessage request)
    {
        if (request.Method != HttpMethod.Get && request.Method != HttpMethod.Head)
            return false;

        var host = request.RequestUri?.Host;
        return host is not null &&
               (IsHostOrSubdomain(host, "github.com") ||
                IsHostOrSubdomain(host, "githubusercontent.com"));
    }

    private static bool IsHostOrSubdomain(string host, string domain) =>
        host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith($".{domain}", StringComparison.OrdinalIgnoreCase);

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.RequestTimeout ||
        (int)statusCode == 429 ||
        (int)statusCode >= 500;

    private static bool IsTransient(Exception exception, CancellationToken cancellationToken) =>
        exception is HttpRequestException ||
        exception is TaskCanceledException && !cancellationToken.IsCancellationRequested;

    private TimeSpan GetDelay(HttpResponseMessage? response, int retryNumber)
    {
        var retryAfter = response?.Headers.RetryAfter;
        var requestedDelay = retryAfter?.Delta;
        if (!requestedDelay.HasValue && retryAfter?.Date is { } retryDate)
            requestedDelay = retryDate - DateTimeOffset.UtcNow;

        var multiplier = 1L << (retryNumber - 1);
        var exponentialDelay = _initialDelay.Ticks > _maximumDelay.Ticks / multiplier
            ? _maximumDelay
            : TimeSpan.FromTicks(_initialDelay.Ticks * multiplier);
        var delay = requestedDelay.GetValueOrDefault() > TimeSpan.Zero
            ? requestedDelay.GetValueOrDefault()
            : exponentialDelay;

        return delay > _maximumDelay ? _maximumDelay : delay;
    }

    private void NotifyRetry(
        HttpRequestMessage request,
        int retryNumber,
        TimeSpan delay,
        string reason,
        Exception? exception)
    {
        if (_onRetry is null)
            return;

        var message =
            $"GitHub request to {request.RequestUri} failed ({reason}); retry {retryNumber}/{_maxRetries} in {delay.TotalSeconds:0.###}s.";
        try
        {
            _onRetry(message, exception);
        }
        catch
        {
            // Diagnostics must not break update recovery.
        }
    }
}
