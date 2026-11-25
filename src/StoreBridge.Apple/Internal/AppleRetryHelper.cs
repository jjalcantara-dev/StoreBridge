using System.Net;

namespace StoreBridge.Apple.Internal;

internal static class AppleRetryHelper
{
    private static readonly int[] DelaySeconds = [1, 2, 3, 5, 8];

    /// <summary>
    /// Executes <paramref name="operation"/> up to <paramref name="maxRetries"/> times, retrying only on
    /// transient failures: network errors, 5xx, 429 Too Many Requests and HTTP timeouts.
    /// Caller cancellation always propagates. A timeout that survives every attempt is rethrown as
    /// <see cref="TimeoutException"/> so callers can tell it apart from their own cancellation.
    /// </summary>
    internal static async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        int maxRetries,
        CancellationToken cancellationToken)
    {
        if (maxRetries < 1)
            maxRetries = 1;

        Exception? lastEx = null;
        TimeSpan? retryAfter = null;

        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            retryAfter = null;
            try
            {
                return await operation(cancellationToken);
            }
            catch (HttpRequestException ex) when (IsTransient(ex))
            {
                lastEx = ex;
                if (ex is AppleHttpException appleEx)
                    retryAfter = appleEx.RetryAfter;
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                // HttpClient.Timeout surfaces as TaskCanceledException without the caller cancelling.
                lastEx = new TimeoutException("The Apple API request timed out.", ex);
            }
            // Non-transient 4xx and caller cancellation propagate immediately — no retry

            if (attempt < maxRetries - 1)
            {
                var delay = retryAfter ?? AddJitter(TimeSpan.FromSeconds(DelaySeconds[Math.Min(attempt, DelaySeconds.Length - 1)]));
                await Task.Delay(delay, cancellationToken);
            }
        }

        throw lastEx!;
    }

    private static TimeSpan AddJitter(TimeSpan baseDelay)
    {
        // ±25% jitter to spread concurrent retries without materially changing backoff duration.
        var factor = 0.75 + Random.Shared.NextDouble() * 0.5;
        return TimeSpan.FromMilliseconds(baseDelay.TotalMilliseconds * factor);
    }

    // Retry on network errors (no HTTP status), server errors (5xx) and rate limiting (429)
    private static bool IsTransient(HttpRequestException ex) =>
        ex.StatusCode is null ||
        ex.StatusCode == HttpStatusCode.TooManyRequests ||
        (int)ex.StatusCode.Value >= 500;
}
