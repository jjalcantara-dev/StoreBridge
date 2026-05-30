using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace StoreBridge.Apple.Internal;

internal static class AppleJwtHelper
{
    internal static string GenerateToken(string keyId, string issuerId, string bundleId, string privateKeyBase64)
    {
        if (string.IsNullOrWhiteSpace(privateKeyBase64))
            throw new InvalidOperationException(
                "Apple PrivateKeyBase64 is not configured. Set it in AppleSubscriptionOptions / AppleInAppPurchaseOptions.");

        using var ecdsa = ECDsa.Create();
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

        try
        {
            ecdsa.ImportPkcs8PrivateKey(keyBytes, out _);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException(
                "Apple PrivateKeyBase64 could not be imported as a PKCS#8 ECDSA key. " +
                "Make sure you downloaded the .p8 from App Store Connect → Users and Access → " +
                "Keys → In-App Purchase, and that the file contents (between the BEGIN and END " +
                "headers) were base64-encoded as-is.", ex);
        }

        var securityKey = new ECDsaSecurityKey(ecdsa) { KeyId = keyId };
        var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.EcdsaSha256);

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var header = new JwtHeader(credentials);
        header["kid"] = keyId;

        var payload = new JwtPayload
        {
            { "iss", issuerId },
            { "iat", now },
            { "exp", now + 1200 }, // 20 minutes
            { "aud", "appstoreconnect-v1" },
            { "bid", bundleId }
        };

        var token = new JwtSecurityToken(header, payload);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>
    /// Extracts and decodes the outer AppStoreNotificationPayload from a raw webhook body.
    /// Accepts either a JSON wrapper <c>{"signedPayload":"..."}</c> or a bare JWT string.
    /// </summary>
    internal static AppStoreNotificationPayload ParseNotificationPayload(string rawBody)
    {
        string signedPayload;

        if (rawBody.TrimStart().StartsWith('{'))
        {
            using var doc = System.Text.Json.JsonDocument.Parse(rawBody);
            if (!doc.RootElement.TryGetProperty("signedPayload", out var sp))
                throw new FormatException("Missing 'signedPayload' field in webhook body.");
            signedPayload = sp.GetString()
                ?? throw new FormatException("'signedPayload' is null.");
        }
        else
        {
            signedPayload = rawBody.Trim();
        }

        return DecodePayload<AppStoreNotificationPayload>(signedPayload)
            ?? throw new FormatException("Failed to decode outer signed payload JWT.");
    }

    internal static string BuildRawEventType(string? type, string? subtype) =>
        string.IsNullOrEmpty(subtype) ? (type ?? string.Empty) : $"{type}:{subtype}";

    /// <summary>Decodes a JWT payload without validating the signature.</summary>
    internal static T? DecodePayload<T>(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length != 3)
            throw new FormatException("Invalid JWT format: expected 3 parts.");

        var payload = parts[1]
            .Replace('-', '+')
            .Replace('_', '/');

        payload = (payload.Length % 4) switch
        {
            2 => payload + "==",
            3 => payload + "=",
            _ => payload
        };

        var bytes = Convert.FromBase64String(payload);
        return JsonSerializer.Deserialize<T>(bytes);
    }
}
