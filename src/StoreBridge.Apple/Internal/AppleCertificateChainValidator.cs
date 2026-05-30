using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace StoreBridge.Apple.Internal;

/// <summary>
/// Validates the X.509 certificate chain embedded in an Apple signed JWT (x5c header)
/// and verifies the JWT's ECDSA signature against the authenticated leaf certificate.
/// </summary>
internal static class AppleCertificateChainValidator
{
    /// <summary>
    /// Validates the certificate chain and ECDSA signature of an Apple-signed JWT.
    /// </summary>
    /// <param name="jwt">The full signed JWT string (header.payload.signature).</param>
    /// <param name="trustedRoots">The trusted Apple Root CA certificates to anchor the chain.</param>
    /// <exception cref="WebhookAuthenticationException">Thrown when chain or signature validation fails.</exception>
    internal static void ValidateSignature(string jwt, IReadOnlyCollection<X509Certificate2> trustedRoots)
    {
        var parts = jwt.Split('.');
        if (parts.Length != 3)
            throw new WebhookAuthenticationException("Invalid JWT format: expected 3 parts.");

        var x5c = ExtractX5c(parts[0]);
        var certs = LoadCertificates(x5c);
        try
        {
            ValidateChain(certs, trustedRoots);
            ValidateLeafExpiry(certs[0]);

            using var publicKey = certs[0].GetECDsaPublicKey()
                ?? throw new WebhookAuthenticationException("Leaf certificate does not contain an EC public key.");

            VerifyJwtSignature(parts[0], parts[1], parts[2], publicKey);
        }
        finally
        {
            foreach (var cert in certs)
                cert.Dispose();
        }
    }

    private static string[] ExtractX5c(string headerBase64Url)
    {
        var headerBytes = Base64UrlDecode(headerBase64Url);
        using var doc = JsonDocument.Parse(headerBytes);

        if (!doc.RootElement.TryGetProperty("x5c", out var x5cProp) ||
            x5cProp.ValueKind != JsonValueKind.Array)
            throw new WebhookAuthenticationException(
                "Apple notification JWT header is missing the 'x5c' certificate chain.");

        var certs = x5cProp.EnumerateArray()
            .Select(e => e.GetString() ?? throw new WebhookAuthenticationException("Null entry in x5c array."))
            .ToArray();

        if (certs.Length == 0)
            throw new WebhookAuthenticationException("Apple notification JWT has an empty 'x5c' certificate chain.");

        return certs;
    }

    private static List<X509Certificate2> LoadCertificates(string[] x5c)
    {
        // x5c values are standard base64-encoded DER (not base64url)
        return x5c
            .Select(c => LoadCertificate(Convert.FromBase64String(c)))
            .ToList();
    }

    private static X509Certificate2 LoadCertificate(byte[] der) =>
#if NET9_0_OR_GREATER
        X509CertificateLoader.LoadCertificate(der);
#else
        new(der);
#endif

    private static void ValidateChain(List<X509Certificate2> certs, IReadOnlyCollection<X509Certificate2> trustedRoots)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        foreach (var root in trustedRoots)
            chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;

        // Add all certs except the leaf to the extra store so the engine can build the path
        foreach (var intermediate in certs.Skip(1))
            chain.ChainPolicy.ExtraStore.Add(intermediate);

        if (!chain.Build(certs[0]))
        {
            var errors = string.Join("; ", chain.ChainStatus.Select(s => s.StatusInformation.Trim()));
            throw new WebhookAuthenticationException(
                $"Apple certificate chain validation failed: {errors}");
        }
    }

    private static void ValidateLeafExpiry(X509Certificate2 leaf)
    {
        var now = DateTime.UtcNow;
        if (now < leaf.NotBefore || now > leaf.NotAfter)
            throw new WebhookAuthenticationException(
                $"Apple signing certificate is not valid at this time (valid {leaf.NotBefore:u} – {leaf.NotAfter:u}).");
    }

    private static void VerifyJwtSignature(string headerB64, string payloadB64, string signatureB64, ECDsa key)
    {
        // The signed content is the ASCII bytes of "header.payload"
        var data = Encoding.ASCII.GetBytes($"{headerB64}.{payloadB64}");

        // JWT ES256 signatures use IEEE P1363 format: raw r || s concatenation (64 bytes for P-256)
        var signature = Base64UrlDecode(signatureB64);

        if (!key.VerifyData(data, signature, HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            throw new WebhookAuthenticationException(
                "Apple notification signature is invalid. The payload may have been tampered with.");
    }

    private static byte[] Base64UrlDecode(string input)
    {
        var s = input.Replace('-', '+').Replace('_', '/');
        s = (s.Length % 4) switch
        {
            2 => s + "==",
            3 => s + "=",
            _ => s
        };
        return Convert.FromBase64String(s);
    }
}
