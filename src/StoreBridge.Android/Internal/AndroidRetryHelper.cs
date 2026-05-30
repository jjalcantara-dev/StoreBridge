using System.Net;

namespace StoreBridge.Android.Internal;

/// <summary>
/// Retry policy for the Google Play Developer API. Mirrors <c>AppleRetryHelper</c>: fibonacci
/// backoff (1, 2, 3, 5, 8 s), retries only on transient failures — 5xx responses, 429 Too Many
/// Requests, and network-level errors. 4xx (other than 429) propagates immediately; cancellation
/// always propagates.
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
            catch (Google.GoogleApiException ex) when (IsTransient(ex))
            {
                lastEx = ex;
            }
            catch (HttpRequestException ex)
            {
                lastEx = ex;
            }
            catch (OperationCanceledException) { throw; }

            if (attempt < maxRetries - 1)
                await Task.Delay(
                    TimeSpan.FromSeconds(DelaySeconds[Math.Min(attempt, DelaySeconds.Length - 1)]),
                    cancellationToken);
        }

        throw lastEx!;
    }

    private static bool IsTransient(Google.GoogleApiException ex)
    {
        // No HTTP status set ⇒ network or pre-flight failure ⇒ worth retrying
        if (ex.HttpStatusCode == 0)
            return true;

        var code = (int)ex.HttpStatusCode;
        return code >= 500 || code == (int)HttpStatusCode.TooManyRequests;
    }
}
