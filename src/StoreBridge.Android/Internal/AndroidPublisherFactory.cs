using Google.Apis.AndroidPublisher.v3;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Http;
using Google.Apis.Services;
using System.Security.Cryptography;

namespace StoreBridge.Android.Internal;

internal static class AndroidPublisherFactory
{
    private static readonly string Scope = AndroidPublisherService.Scope.Androidpublisher;

    /// <summary>
    /// Builds the Play Developer API client from a base64-encoded service-account JSON key.
    /// Only the <c>service_account</c> credential type is accepted.
    /// </summary>
    /// <exception cref="FormatException">The value is not valid base64.</exception>
    /// <exception cref="InvalidOperationException">The JSON is not a valid service-account key.</exception>
    internal static AndroidPublisherService Create(string credentialsBase64)
    {
        var bytes = Convert.FromBase64String(credentialsBase64);
        GoogleCredential credential;
        try
        {
            using var stream = new MemoryStream(bytes);
            credential = CredentialFactory.FromStream<ServiceAccountCredential>(stream)
                .ToGoogleCredential()
                .CreateScoped(Scope);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }

        return new AndroidPublisherService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "StoreBridge",
            // Retries are managed by AndroidRetryHelper; disable the SDK's built-in 503 retries
            // so transient failures don't multiply into 3x3 = 9 requests.
            DefaultExponentialBackOffPolicy = ExponentialBackOffPolicy.None
        });
    }
}
