using Google.Apis.Auth;

namespace StoreBridge.Android.Internal;

internal interface IGoogleTokenValidator
{
    Task<GoogleJsonWebSignature.Payload> ValidateAsync(
        string token,
        string expectedAudience,
        CancellationToken cancellationToken);
}
