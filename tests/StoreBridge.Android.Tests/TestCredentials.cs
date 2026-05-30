using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace StoreBridge.Android.Tests;

/// <summary>
/// Builds a syntactically valid Google service-account JSON (with a freshly generated RSA key)
/// so the <c>GoogleCredential.FromStream</c> path inside the options validator accepts it.
/// The key is never sent anywhere — tests intercept the Google API client via
/// <see cref="StoreBridge.Android.Internal.IAndroidSubscriptionsv2"/> / <see cref="StoreBridge.Android.Internal.IAndroidProducts"/>.
/// </summary>
internal static class TestCredentials
{
    internal static string ServiceAccountBase64 { get; } = BuildServiceAccountBase64();

    private static string BuildServiceAccountBase64()
    {
        using var rsa = RSA.Create(2048);
        var pem = "-----BEGIN PRIVATE KEY-----\n" +
                  Convert.ToBase64String(rsa.ExportPkcs8PrivateKey(), Base64FormattingOptions.InsertLineBreaks) +
                  "\n-----END PRIVATE KEY-----\n";

        var json = JsonSerializer.Serialize(new
        {
            type = "service_account",
            project_id = "storebridge-tests",
            private_key_id = "test-key-id",
            private_key = pem,
            client_email = "tests@storebridge-tests.iam.gserviceaccount.com",
            client_id = "0",
            auth_uri = "https://accounts.google.com/o/oauth2/auth",
            token_uri = "https://oauth2.googleapis.com/token"
        });

        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }
}
