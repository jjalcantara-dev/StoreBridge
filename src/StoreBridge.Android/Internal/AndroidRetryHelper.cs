using System.Net;

namespace StoreBridge.Android.Internal;

/// <summary>
/// Retry policy for the Google Play Developer API. Mirrors <c>AppleRetryHelper</c>: fibonacci
/// backoff (1, 2, 3, 5, 8 s), retries only on transient failures — 5xx responses, 429 Too Many
/// Requests, network-level errors and HTTP timeouts. 4xx (other than 429) propagates immediately;
/// caller cancellation always propagates. A timeout that survives every attempt is rethrown as
/// <see cref="TimeoutException"/>.
/// </summary>
internal static class AndroidRetryHelper
{
    private static readonly int[] DelaySeconds = [1, 2, 3, 5, 8];

    internal static async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        int maxRetries,
        CancellationToken cancellationToken)
    {
        if (maxRetries < 1)
            maxRetries = 1;

        Exception? lastEx = null;

        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                return await operation(cancellationToken);
            }
            catch (Google.GoogleApiException ex) when (IsTransient(ex.HttpStatusCode))
            {
                lastEx = ex;
            }
            catch (HttpRequestException ex) when (ex.StatusCode is null || IsTransient(ex.StatusCode.Value))
            {
                lastEx = ex;
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                // The Google client's HttpClient timeout surfaces as TaskCanceledException.
                lastEx = new TimeoutException("The Google Play Developer API request timed out.", ex);
            }

            if (attempt < maxRetries - 1)
                await Task.Delay(
                    AddJitter(TimeSpan.FromSeconds(DelaySeconds[Math.Min(attempt, DelaySeconds.Length - 1)])),
                    cancellationToken);
        }

        throw lastEx!;
    }

    private static TimeSpan AddJitter(TimeSpan baseDelay)
    {
        // ±25% jitter to spread concurrent retries without materially changing backoff duration.
        var factor = 0.75 + Random.Shared.NextDouble() * 0.5;
        return TimeSpan.FromMilliseconds(baseDelay.TotalMilliseconds * factor);
    }

    // No HTTP status set ⇒ network or pre-flight failure ⇒ worth retrying
    private static bool IsTransient(HttpStatusCode status) =>
        status == 0 || (int)status >= 500 || status == HttpStatusCode.TooManyRequests;
}
