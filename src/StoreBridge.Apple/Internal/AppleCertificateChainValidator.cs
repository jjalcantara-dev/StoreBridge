using System.Buffers.Text;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace StoreBridge.Apple.Internal;

/// <summary>
/// Validates the X.509 certificate chain embedded in an Apple signed JWT (x5c header)
/// and verifies the JWT's ECDSA signature against the authenticated leaf certificate.
/// </summary>
/// <remarks>
/// Mirrors the checks performed by Apple's official App Store Server Libraries:
/// the x5c chain must contain exactly three certificates (leaf, intermediate, root), the leaf
/// must carry the App Store receipt-signing marker OID and the intermediate the Apple WWDR
/// intermediate marker OID. Without the OID checks, any EC certificate issued under Apple Root
/// CA - G3 (which also anchors other Apple PKI hierarchies) would be accepted.
/// </remarks>
internal static class AppleCertificateChainValidator
{
    /// <summary>Number of certificates Apple puts in the x5c header: leaf, intermediate, root.</summary>
    internal const int ExpectedChainLength = 3;

    /// <summary>Marker extension present on Apple's App Store signing (leaf) certificates.</summary>
    internal const string LeafMarkerOid = "1.2.840.113635.100.6.11.1";

    /// <summary>Marker extension present on the Apple Worldwide Developer Relations intermediate CA.</summary>
    internal const string IntermediateMarkerOid = "1.2.840.113635.100.6.2.1";

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

        var certs = LoadCertificates(ParseX5c(parts[0]));
        try
        {
            ValidateMarkerExtensions(certs);
            ValidateChain(certs, trustedRoots);

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

    private static string[] ParseX5c(string headerBase64Url)
    {
        byte[] headerBytes;
        try
        {
            headerBytes = Base64Url.DecodeFromChars(headerBase64Url);
        }
        catch (FormatException ex)
        {
            throw new WebhookAuthenticationException("Apple notification JWT header is not valid base64url.", ex);
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(headerBytes);
        }
        catch (JsonException ex)
        {
            throw new WebhookAuthenticationException("Apple notification JWT header is not valid JSON.", ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new WebhookAuthenticationException("Apple notification JWT header is not a JSON object.");

            var alg = root.TryGetProperty("alg", out var algProp) && algProp.ValueKind == JsonValueKind.String
                ? algProp.GetString()
                : null;
            if (!string.Equals(alg, "ES256", StringComparison.Ordinal))
                throw new WebhookAuthenticationException(
                    $"Apple notification JWT uses unsupported algorithm '{alg ?? "(none)"}'; expected ES256.");

            if (!root.TryGetProperty("x5c", out var x5cProp) || x5cProp.ValueKind != JsonValueKind.Array)
                throw new WebhookAuthenticationException(
                    "Apple notification JWT header is missing the 'x5c' certificate chain.");

            if (x5cProp.GetArrayLength() != ExpectedChainLength)
                throw new WebhookAuthenticationException(
                    $"Apple notification JWT 'x5c' must contain exactly {ExpectedChainLength} certificates " +
                    $"(leaf, intermediate, root); got {x5cProp.GetArrayLength()}.");

            var certs = x5cProp.EnumerateArray()
                .Select(e => e.ValueKind == JsonValueKind.String
                    ? e.GetString()!
                    : throw new WebhookAuthenticationException("Non-string entry in x5c array."))
                .ToArray();

            return certs;
        }
    }

    private static List<X509Certificate2> LoadCertificates(string[] x5c)
    {
        var certs = new List<X509Certificate2>(x5c.Length);
        try
        {
            // x5c values are standard base64-encoded DER (not base64url)
            foreach (var entry in x5c)
                certs.Add(X509CertificateLoader.LoadCertificate(Convert.FromBase64String(entry)));
            return certs;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            foreach (var cert in certs)
                cert.Dispose();
            throw new WebhookAuthenticationException("Apple notification x5c contains an invalid certificate.", ex);
        }
    }

    private static void ValidateMarkerExtensions(List<X509Certificate2> certs)
    {
        if (certs[0].Extensions[LeafMarkerOid] is null)
            throw new WebhookAuthenticationException(
                $"Apple signing certificate is missing the App Store marker extension ({LeafMarkerOid}).");

        if (certs[1].Extensions[IntermediateMarkerOid] is null)
            throw new WebhookAuthenticationException(
                $"Apple intermediate certificate is missing the WWDR marker extension ({IntermediateMarkerOid}).");
    }

    private static void ValidateChain(List<X509Certificate2> certs, IReadOnlyCollection<X509Certificate2> trustedRoots)
    {
        var leaf = certs[0];
        var intermediate = certs[1];

        // Try each trusted root individually. If all roots are added to a single CustomTrustStore,
        // the platform chain engine (especially OpenSSL on Linux) may pick a root whose subject name
        // matches but whose key differs, producing a spurious "certificate signature failure" instead
        // of falling through to the correct root.
        string? lastErrors = null;
        foreach (var root in trustedRoots)
        {
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(root);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            // Never follow AIA URLs from attacker-supplied certificates (blind SSRF / latency).
            chain.ChainPolicy.DisableCertificateDownloads = true;
            // Only the intermediate from x5c is offered; x5c[2] (the root Apple embeds) is ignored
            // on purpose — trust comes exclusively from the pinned roots.
            chain.ChainPolicy.ExtraStore.Add(intermediate);

            if (chain.Build(leaf))
            {
                // The chain must be exactly leaf → x5c intermediate → pinned root.
                if (chain.ChainElements.Count == ExpectedChainLength &&
                    chain.ChainElements[1].Certificate.RawDataMemory.Span.SequenceEqual(intermediate.RawDataMemory.Span))
                {
                    return;
                }

                lastErrors = "chain did not pass through the x5c intermediate certificate";
                continue;
            }

            lastErrors = string.Join("; ", chain.ChainStatus.Select(s => s.StatusInformation.Trim()));
        }

        throw new WebhookAuthenticationException(
            $"Apple certificate chain validation failed: {lastErrors ?? "unknown error"}");
    }

    private static void VerifyJwtSignature(string headerB64, string payloadB64, string signatureB64, ECDsa key)
    {
        // The signed content is the ASCII bytes of "header.payload"
        var data = Encoding.ASCII.GetBytes($"{headerB64}.{payloadB64}");

        byte[] signature;
        try
        {
            // JWT ES256 signatures use IEEE P1363 format: raw r || s concatenation (64 bytes for P-256)
            signature = Base64Url.DecodeFromChars(signatureB64);
        }
        catch (FormatException ex)
        {
            throw new WebhookAuthenticationException("Apple notification signature is not valid base64url.", ex);
        }

        if (!key.VerifyData(data, signature, HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            throw new WebhookAuthenticationException(
                "Apple notification signature is invalid. The payload may have been tampered with.");
    }
}
