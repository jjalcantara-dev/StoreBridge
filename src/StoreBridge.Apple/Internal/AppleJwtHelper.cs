using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace StoreBridge.Apple.Internal;

internal static class AppleJwtHelper
{
    /// <summary>Lifetime of the App Store Server API bearer token (Apple allows up to 60 minutes).</summary>
    internal static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(20);

    /// <summary>
    /// Builds the ES256 bearer token for the App Store Server API.
    /// See https://developer.apple.com/documentation/appstoreserverapi/generating-json-web-tokens-for-api-requests
    /// </summary>
    internal static string GenerateToken(string keyId, string issuerId, string bundleId, string privateKeyBase64)
    {
        if (string.IsNullOrWhiteSpace(privateKeyBase64))
            throw new InvalidOperationException(
                "Apple PrivateKeyBase64 is not configured. Set it in AppleSubscriptionOptions / AppleInAppPurchaseOptions.");

        byte[] keyBytes;
        try
        {
            keyBytes = Convert.FromBase64String(privateKeyBase64);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(
                "Apple PrivateKeyBase64 is not valid base64. Encode the body of the .p8 file " +
                "(without the BEGIN/END PRIVATE KEY headers) as base64.", ex);
        }

        using var ecdsa = ECDsa.Create();
        try
        {
            ecdsa.ImportPkcs8PrivateKey(keyBytes, out _);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException(
                "Apple PrivateKeyBase64 could not be imported as a PKCS#8 ECDSA key. " +
                "Make sure you downloaded the .p8 from App Store Connect → Users and Access → " +
                "Integrations → In-App Purchase, and that the file contents (between the BEGIN and END " +
                "headers) were base64-encoded as-is.", ex);
        }

        if (ecdsa.KeySize != 256)
            throw new InvalidOperationException(
                $"Apple PrivateKeyBase64 must be a P-256 (256-bit) ECDSA key for ES256 signing (got {ecdsa.KeySize}-bit).");

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var header = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["alg"] = "ES256",
            ["kid"] = keyId,
            ["typ"] = "JWT"
        });
        var payload = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["iss"] = issuerId,
            ["iat"] = now,
            ["exp"] = now + (long)TokenLifetime.TotalSeconds,
            ["aud"] = "appstoreconnect-v1",
            ["bid"] = bundleId
        });

        var signingInput = $"{Base64Url.EncodeToString(header)}.{Base64Url.EncodeToString(payload)}";
        var signature = ecdsa.SignData(
            Encoding.ASCII.GetBytes(signingInput),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        return $"{signingInput}.{Base64Url.EncodeToString(signature)}";
    }

    /// <summary>
    /// Extracts the signed payload JWT from a raw webhook body.
    /// Accepts either a JSON wrapper <c>{"signedPayload":"..."}</c> or a bare JWT string.
    /// </summary>
    /// <exception cref="FormatException">The body is not valid JSON, or <c>signedPayload</c> is missing or not a string.</exception>
    internal static string ExtractSignedPayload(string rawBody)
    {
        if (!rawBody.TrimStart().StartsWith('{'))
            return rawBody.Trim();

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(rawBody);
        }
        catch (JsonException ex)
        {
            throw new FormatException($"Webhook body is not valid JSON: {ex.Message}", ex);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("signedPayload", out var sp))
                throw new FormatException("Missing 'signedPayload' field in webhook body.");

            if (sp.ValueKind != JsonValueKind.String)
                throw new FormatException("'signedPayload' must be a string.");

            try
            {
                return sp.GetString()!;
            }
            catch (InvalidOperationException ex)
            {
                // e.g. an escaped lone surrogate ("\ud800") that cannot be transcoded to UTF-16
                throw new FormatException("'signedPayload' is not a valid JSON string.", ex);
            }
        }
    }

    /// <summary>
    /// Extracts and decodes the outer AppStoreNotificationPayload from a raw webhook body.
    /// Accepts either a JSON wrapper <c>{"signedPayload":"..."}</c> or a bare JWT string.
    /// </summary>
    internal static AppStoreNotificationPayload ParseNotificationPayload(string rawBody) =>
        DecodePayload<AppStoreNotificationPayload>(ExtractSignedPayload(rawBody))
            ?? throw new FormatException("Failed to decode outer signed payload JWT.");

    internal static string BuildRawEventType(string? type, string? subtype) =>
        string.IsNullOrEmpty(subtype) ? (type ?? string.Empty) : $"{type}:{subtype}";

    /// <summary>
    /// Converts a Unix millisecond timestamp to <see cref="DateTimeOffset"/>, wrapping out-of-range
    /// values into <see cref="FormatException"/>.
    /// </summary>
    internal static DateTimeOffset? FromUnixTimeMilliseconds(long? millis)
    {
        if (!millis.HasValue)
            return null;

        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(millis.Value);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new FormatException($"Unix timestamp '{millis.Value}' is out of range.", ex);
        }
    }

    /// <summary>
    /// Whether an Apple <c>environment</c> value denotes a non-production environment
    /// (<c>Sandbox</c>, <c>Xcode</c>, <c>LocalTesting</c>, …).
    /// </summary>
    internal static bool IsNonProduction(string? environment) =>
        !string.IsNullOrEmpty(environment) &&
        !string.Equals(environment, "Production", StringComparison.OrdinalIgnoreCase);

    /// <summary>Decodes a JWT payload without validating the signature.</summary>
    /// <exception cref="FormatException">The JWT is malformed or its payload does not match <typeparamref name="T"/>.</exception>
    internal static T? DecodePayload<T>(string jwt)
    {
        var bytes = DecodePayloadBytes(jwt);
        try
        {
            return JsonSerializer.Deserialize<T>(bytes);
        }
        catch (JsonException ex)
        {
            throw new FormatException($"JWT payload is not a valid {typeof(T).Name}: {ex.Message}", ex);
        }
    }

    /// <summary>Returns the raw UTF-8 JSON bytes of a JWT payload without validating the signature.</summary>
    internal static byte[] DecodePayloadBytes(string jwt)
    {
        var parts = jwt.Split('.', 4);
        if (parts.Length != 3)
            throw new FormatException("Invalid JWT format: expected 3 parts.");

        return Base64Url.DecodeFromChars(parts[1]);
    }
}

/// <summary>
/// Thread-safe per-verifier cache for the App Store Server API ES256 bearer JWT.
/// Reuses the signed token for 15 minutes (leaving a 5-minute buffer before the 20-minute <c>exp</c>).
/// </summary>
internal sealed class AppleTokenCache
{
    private static readonly TimeSpan RefreshBuffer = TimeSpan.FromMinutes(5);

    private readonly Lock _lock = new();
    private string? _cachedToken;
    private DateTimeOffset _expiresAtUtc;

    internal string GetOrCreate(AppleApiOptions options, TimeProvider? timeProvider = null)
    {
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();

        lock (_lock)
        {
            if (_cachedToken is not null && now < _expiresAtUtc - RefreshBuffer)
                return _cachedToken;

            var token = AppleJwtHelper.GenerateToken(
                options.KeyId, options.IssuerId, options.BundleId, options.PrivateKeyBase64);
            _cachedToken = token;
            _expiresAtUtc = now + AppleJwtHelper.TokenLifetime;
            return token;
        }
    }
}
