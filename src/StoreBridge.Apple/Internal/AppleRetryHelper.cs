namespace StoreBridge.Apple.Internal;

internal static class AppleRetryHelper
{
    private static readonly int[] DelaySeconds = [1, 2, 3, 5, 8];

    /// <summary>
    /// Executes <paramref name="operation"/> up to <paramref name="maxRetries"/> times,
    /// retrying only on <see cref="HttpRequestException"/>. Re-throws on <see cref="TaskCanceledException"/>.
    /// Throws the last <see cref="HttpRequestException"/> after all retries are exhausted.
    /// </summary>
    internal static async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        int maxRetries,
        CancellationToken cancellationToken)
    {
        Exception? lastEx = null;

        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                return await operation(cancellationToken);
            }
            catch (HttpRequestException ex) when (IsTransient(ex))
            {
                lastEx = ex;
            }
            catch (TaskCanceledException) { throw; }
            // Non-transient 4xx propagates immediately — no retry

            if (attempt < maxRetries - 1)
                await Task.Delay(
                    TimeSpan.FromSeconds(DelaySeconds[Math.Min(attempt, DelaySeconds.Length - 1)]),
                    cancellationToken);
        }

        throw lastEx!;
    }

    // Only retry on network errors (no HTTP status) or server errors (5xx)
    private static bool IsTransient(HttpRequestException ex) =>
        ex.StatusCode == null || (int)ex.StatusCode.Value >= 500;
}
