using Google.Apis.AndroidPublisher.v3;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;

namespace StoreBridge.Android.Internal;

internal static class AndroidPublisherFactory
{
    private static readonly string Scope = AndroidPublisherService.Scope.Androidpublisher;

    internal static AndroidPublisherService Create(string credentialsBase64)
    {
        var bytes = Convert.FromBase64String(credentialsBase64);
        using var stream = new MemoryStream(bytes);
#pragma warning disable CS0618 // CredentialFactory alternative is async-only; credentials are always our own service account JSON
        var credential = GoogleCredential.FromStream(stream).CreateScoped(Scope);
#pragma warning restore CS0618
        return new AndroidPublisherService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential
        });
    }
}
