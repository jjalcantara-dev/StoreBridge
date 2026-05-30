using StoreBridge.Apple;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace StoreBridge.Apple.Tests;

public sealed class AppleWebhookAuthenticatorTests
{
    [Fact]
    public void Store_IsApple()
    {
        using var authenticator = new AppleWebhookAuthenticator(BuildFakeRootCaDer());
        Assert.Equal(Store.Apple, authenticator.Store);
    }

    [Fact]
    public async Task ValidateAsync_EmptyBody_ThrowsArgumentException()
    {
        using var authenticator = new AppleWebhookAuthenticator(BuildFakeRootCaDer());
        await Assert.ThrowsAsync<ArgumentException>(() => authenticator.ValidateAsync(string.Empty));
    }

    [Fact]
    public async Task ValidateAsync_MissingSignedPayload_ThrowsFormatException()
    {
        using var authenticator = new AppleWebhookAuthenticator(BuildFakeRootCaDer());
        await Assert.ThrowsAsync<FormatException>(() => authenticator.ValidateAsync("{}"));
    }

    [Fact]
    public async Task ValidateAsync_JwtMissingX5c_ThrowsWebhookAuthenticationException()
    {
        using var authenticator = new AppleWebhookAuthenticator(BuildFakeRootCaDer());

        // A JWT whose header has no x5c field
        var jwt = BuildJwtWithHeader("""{"alg":"ES256"}""", fakeSignature: true);
        var body = $$"""{"signedPayload":"{{jwt}}"}""";

        await Assert.ThrowsAsync<WebhookAuthenticationException>(() => authenticator.ValidateAsync(body));
    }

    [Fact]
    public async Task ValidateAsync_ChainNotTrustedByRoot_ThrowsWebhookAuthenticationException()
    {
        // Use a different root than the one used to sign the notification chain
        var (_, _, differentRootDer) = BuildFakeChain();
        using var authenticator = new AppleWebhookAuthenticator(differentRootDer);

        // Build a valid chain signed by a DIFFERENT root
        var (jwt, _, _) = BuildSignedNotification();
        var body = $$"""{"signedPayload":"{{jwt}}"}""";

        await Assert.ThrowsAsync<WebhookAuthenticationException>(() => authenticator.ValidateAsync(body));
    }

    [Fact]
    public async Task ValidateAsync_TamperedPayload_ThrowsWebhookAuthenticationException()
    {
        var (jwt, _, rootDer) = BuildSignedNotification();
        using var authenticator = new AppleWebhookAuthenticator(rootDer);

        // Swap the payload part with a different one (tampering)
        var parts = jwt.Split('.');
        var tamperedPayload = Base64UrlEncode("""{"notificationType":"FAKE","injected":true}""");
        var tamperedJwt = $"{parts[0]}.{tamperedPayload}.{parts[2]}";
        var body = $$"""{"signedPayload":"{{tamperedJwt}}"}""";

        await Assert.ThrowsAsync<WebhookAuthenticationException>(() => authenticator.ValidateAsync(body));
    }

    [Fact]
    public async Task ValidateAsync_ValidSignatureAndChain_DoesNotThrow()
    {
        var (jwt, _, rootDer) = BuildSignedNotification();
        using var authenticator = new AppleWebhookAuthenticator(rootDer);

        var body = $$"""{"signedPayload":"{{jwt}}"}""";

        // Should complete without throwing
        await authenticator.ValidateAsync(body);
    }

    [Fact]
    public async Task ValidateAsync_BearerTokenIsIgnoredForApple()
    {
        var (jwt, _, rootDer) = BuildSignedNotification();
        using var authenticator = new AppleWebhookAuthenticator(rootDer);

        var body = $$"""{"signedPayload":"{{jwt}}"}""";

        // bearerToken is irrelevant for Apple — signature is embedded in the JWT
        await authenticator.ValidateAsync(body, bearerToken: "ignored-token");
    }

    // ──────────────── nested signed JWT verification ────────────────

    [Fact]
    public async Task ValidateAsync_NestedTransactionSignedBySameChain_DoesNotThrow()
    {
        var (jwt, rootDer) = BuildSignedNotificationWithNestedTransaction(nestedFromDifferentChain: false);
        using var authenticator = new AppleWebhookAuthenticator(rootDer);

        await authenticator.ValidateAsync($$"""{"signedPayload":"{{jwt}}"}""");
    }

    [Fact]
    public async Task ValidateAsync_NestedTransactionSignedByDifferentChain_Throws()
    {
        // Outer JWT is validly signed, but the nested signedTransactionInfo is signed
        // by an unrelated chain — nested verification must catch this.
        var (jwt, rootDer) = BuildSignedNotificationWithNestedTransaction(nestedFromDifferentChain: true);
        using var authenticator = new AppleWebhookAuthenticator(rootDer);

        await Assert.ThrowsAsync<WebhookAuthenticationException>(
            () => authenticator.ValidateAsync($$"""{"signedPayload":"{{jwt}}"}"""));
    }

    // ──────────────── multiple trusted roots ────────────────

    [Fact]
    public async Task ValidateAsync_MultipleRoots_AcceptsChainToAnyTrustedRoot()
    {
        var (jwt, _, rootDer) = BuildSignedNotification();
        var (_, _, unrelatedRootDer) = BuildFakeChain();

        // The notification chains to rootDer; the authenticator trusts an unrelated root plus rootDer
        using var authenticator = new AppleWebhookAuthenticator(new[] { unrelatedRootDer, rootDer });

        await authenticator.ValidateAsync($$"""{"signedPayload":"{{jwt}}"}""");
    }

    [Fact]
    public void Constructor_EmptyRootCollection_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new AppleWebhookAuthenticator(Array.Empty<byte[]>()));
    }

    // ──────────────────────── helpers ────────────────────────

    /// <summary>
    /// Builds a signed notification whose payload contains a nested <c>signedTransactionInfo</c>.
    /// When <paramref name="nestedFromDifferentChain"/> is true, the nested JWT is signed by an
    /// unrelated chain, so nested-signature validation should fail.
    /// </summary>
    private static (string jwt, byte[] rootDer) BuildSignedNotificationWithNestedTransaction(bool nestedFromDifferentChain)
    {
        var (leaf, leafKey, rootDer) = BuildFakeChain();

        string nestedJwt;
        if (nestedFromDifferentChain)
        {
            var (otherLeaf, otherKey, _) = BuildFakeChain();
            nestedJwt = SignJwt(new { productId = "premium_monthly", transactionId = "tx-1" }, otherLeaf, otherKey);
        }
        else
        {
            nestedJwt = SignJwt(new { productId = "premium_monthly", transactionId = "tx-1" }, leaf, leafKey);
        }

        var outerJwt = SignJwt(new
        {
            notificationType = "DID_RENEW",
            signedDate = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            data = new { signedTransactionInfo = nestedJwt }
        }, leaf, leafKey);

        return (outerJwt, rootDer);
    }

    /// <summary>Signs an arbitrary payload as an ES256 JWT with the leaf cert embedded in the x5c header.</summary>
    private static string SignJwt(object payload, X509Certificate2 leaf, ECDsa leafKey)
    {
        var leafDer = Convert.ToBase64String(leaf.Export(X509ContentType.Cert));
        var header = Base64UrlEncode(JsonSerializer.Serialize(new { alg = "ES256", x5c = new[] { leafDer } }));
        var body = Base64UrlEncode(JsonSerializer.Serialize(payload));
        var dataToSign = Encoding.ASCII.GetBytes($"{header}.{body}");
        var signature = leafKey.SignData(dataToSign, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return $"{header}.{body}.{Base64UrlEncode(signature)}";
    }

    /// <summary>
    /// Builds a fake self-signed root CA (EC P-256) for tests.
    /// Returns a different root on each call.
    /// </summary>
    private static byte[] BuildFakeRootCaDer()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=FakeRoot", key, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return cert.Export(X509ContentType.Cert);
    }

    /// <summary>
    /// Builds a leaf + root fake chain. Returns (leafCert, leafKey, rootDer).
    /// </summary>
    private static (X509Certificate2 leaf, ECDsa leafKey, byte[] rootDer) BuildFakeChain()
    {
        using var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var rootReq = new CertificateRequest("CN=FakeAppleRoot", rootKey, HashAlgorithmName.SHA256);
        rootReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var rootCert = rootReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(2));

        var leafKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var leafReq = new CertificateRequest("CN=FakeLeaf", leafKey, HashAlgorithmName.SHA256);
        var leafCert = leafReq.Create(rootCert, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1),
            [0x01, 0x02, 0x03]);

        // Return the leaf with private key attached (needed for signing)
        var leafWithKey = leafCert.CopyWithPrivateKey(leafKey);
        return (leafWithKey, leafKey, rootCert.Export(X509ContentType.Cert));
    }

    /// <summary>
    /// Builds a fully signed fake notification JWT using a generated chain.
    /// Returns (jwt, leafCert, rootDer).
    /// </summary>
    private static (string jwt, X509Certificate2 leaf, byte[] rootDer) BuildSignedNotification()
    {
        var (leaf, leafKey, rootDer) = BuildFakeChain();

        var leafDer = Convert.ToBase64String(leaf.Export(X509ContentType.Cert));
        var header = Base64UrlEncode(JsonSerializer.Serialize(new
        {
            alg = "ES256",
            x5c = new[] { leafDer }
        }));

        var payload = Base64UrlEncode(JsonSerializer.Serialize(new
        {
            notificationType = "DID_RENEW",
            signedDate = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        }));

        var dataToSign = Encoding.ASCII.GetBytes($"{header}.{payload}");
        var signature = leafKey.SignData(dataToSign, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var signatureB64 = Base64UrlEncode(signature);

        return ($"{header}.{payload}.{signatureB64}", leaf, rootDer);
    }

    private static string BuildJwtWithHeader(string headerJson, bool fakeSignature = false)
    {
        var header = Base64UrlEncode(headerJson);
        var payload = Base64UrlEncode("""{"test":true}""");
        var sig = fakeSignature ? Base64UrlEncode("fakesig") : Base64UrlEncode(new byte[64]);
        return $"{header}.{payload}.{sig}";
    }

    private static string Base64UrlEncode(string input) =>
        Base64UrlEncode(Encoding.UTF8.GetBytes(input));

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
