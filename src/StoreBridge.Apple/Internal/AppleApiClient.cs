using System.Net;
using System.Net.Http.Headers;

namespace StoreBridge.Apple.Internal;

/// <summary>
/// Shared GET logic for the App Store Server API: bearer auth, retries, error mapping and the
/// production → sandbox fallback Apple recommends for App Review / TestFlight transactions.
/// </summary>
internal static class AppleApiClient
{
    /// <summary>
    /// GETs <c>{baseUrl}/{id}</c> and returns the response body. On <c>404</c> from
    /// <paramref name="primaryBaseUrl"/>, retries against <paramref name="sandboxBaseUrl"/> when one is given
    /// and it differs from the primary URL.
    /// </summary>
    /// <exception cref="HttpRequestException">Non-success response after retries (carries the status code).</exception>
    /// <exception cref="TimeoutException">Every attempt timed out.</exception>
    internal static async Task<string> GetAsync(
        HttpClient client,
        string jwt,
        string primaryBaseUrl,
        string? sandboxBaseUrl,
        string id,
        int maxRetries,
        CancellationToken cancellationToken)
    {
        try
        {
            return await AppleRetryHelper.ExecuteAsync(
                ct => SendAsync(client, jwt, primaryBaseUrl, id, ct), maxRetries, cancellationToken);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound
                                              && ShouldFallBack(primaryBaseUrl, sandboxBaseUrl))
        {
            return await AppleRetryHelper.ExecuteAsync(
                ct => SendAsync(client, jwt, sandboxBaseUrl!, id, ct), maxRetries, cancellationToken);
        }
    }

    private static bool ShouldFallBack(string primaryBaseUrl, string? sandboxBaseUrl) =>
        !string.IsNullOrWhiteSpace(sandboxBaseUrl) &&
        !string.Equals(primaryBaseUrl.TrimEnd('/'), sandboxBaseUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    private static async Task<string> SendAsync(
        HttpClient client, string jwt, string baseUrl, string id, CancellationToken cancellationToken)
    {
        if (id is "." or "..")
            throw new HttpRequestException($"Invalid Apple transaction identifier '{id}'.", inner: null, statusCode: HttpStatusCode.BadRequest);

        // Escape the identifier so a crafted value cannot alter the request path.
        var url = $"{baseUrl.TrimEnd('/')}/{Uri.EscapeDataString(id)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Apple API returned {(int)response.StatusCode}: {body}",
                inner: null,
                statusCode: response.StatusCode);

        return body;
    }
}
