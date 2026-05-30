using Google.Apis.Auth;

namespace StoreBridge.Android.Internal;

internal sealed class GoogleTokenValidator : IGoogleTokenValidator
{
    public async Task<GoogleJsonWebSignature.Payload> ValidateAsync(
        string token,
        string expectedAudience,
        CancellationToken cancellationToken)
    {
        var settings = new GoogleJsonWebSignature.ValidationSettings
        {
            Audience = [expectedAudience]
        };

        // GoogleJsonWebSignature.ValidateAsync fetches and caches Google's public keys internally.
        // It validates: signature, iss (accounts.google.com), aud, exp.
        return await GoogleJsonWebSignature.ValidateAsync(token, settings);
    }
}
