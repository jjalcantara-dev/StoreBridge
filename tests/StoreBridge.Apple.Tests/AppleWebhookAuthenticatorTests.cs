using StoreBridge.Apple;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace StoreBridge.Apple.Tests;

public sealed class AppleWebhookAuthenticatorTests
{
    private const string BundleId = "com.example.app";
    private const string LeafOid = "1.2.840.113635.100.6.11.1";
    private const string IntermediateOid = "1.2.840.113635.100.6.2.1";

    private static AppleWebhookAuthenticatorOptions Options(Action<AppleWebhookAuthenticatorOptions>? configure = null)
    {
        var o = new AppleWebhookAuthenticatorOptions { BundleId = BundleId };
        configure?.Invoke(o);
        return o;
    }

    private static object Data(string bundleId = BundleId, string environment = "Production", long? appAppleId = 1234,
        string? signedTransactionInfo = null) =>
        new { bundleId, environment, appAppleId, signedTransactionInfo };

    // ──────────────── construction ────────────────

    [Fact]
    public void Store_IsApple()
    {
        using var authenticator = new AppleWebhookAuthenticator(Options());
        Assert.Equal(Store.Apple, authenticator.Store);
    }

    [Fact]
    public void Constructor_MissingBundleId_Throws()
    {
        Assert.Throws<ArgumentException>(() => new AppleWebhookAuthenticator(new AppleWebhookAuthenticatorOptions()));
    }

    [Fact]
    public void Constructor_EmptyRootCollection_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new AppleWebhookAuthenticator(Options(), Array.Empty<byte[]>()));
    }

    // ──────────────── input shape ────────────────

    [Fact]
    public async Task ValidateAsync_EmptyBody_ThrowsArgumentException()
    {
        using var authenticator = new AppleWebhookAuthenticator(Options());
        await Assert.ThrowsAsync<ArgumentException>(() => authenticator.ValidateAsync(string.Empty));
    }

    [Fact]
    public async Task ValidateAsync_MissingSignedPayload_ThrowsFormatException()
    {
        using var authenticator = new AppleWebhookAuthenticator(Options());
        await Assert.ThrowsAsync<FormatException>(() => authenticator.ValidateAsync("{}"));
    }

    [Fact]
    public async Task ValidateAsync_JwtMissingX5c_ThrowsWebhookAuthenticationException()
    {
        using var authenticator = new AppleWebhookAuthenticator(Options());
        var jwt = $"{B64("""{"alg":"ES256"}""")}.{B64("""{"test":true}""")}.{B64("fakesig")}";

        await Assert.ThrowsAsync<WebhookAuthenticationException>(() => authenticator.ValidateAsync(Wrap(jwt)));
    }

    [Fact]
    public async Task ValidateAsync_NonEs256Algorithm_Throws()
    {
        var chain = FakeAppleChain.Create();
        using var authenticator = chain.Authenticator(Options());
        var jwt = chain.Sign(new { data = Data() }, alg: "HS256");

        await Assert.ThrowsAsync<WebhookAuthenticationException>(() => authenticator.ValidateAsync(Wrap(jwt)));
    }

    // ──────────────── signature and chain ────────────────

    [Fact]
    public async Task ValidateAsync_ValidSignatureAndChain_DoesNotThrow()
    {
        var chain = FakeAppleChain.Create();
        using var authenticator = chain.Authenticator(Options());

        await authenticator.ValidateAsync(Wrap(chain.Sign(new { notificationType = "DID_RENEW", data = Data() })));
    }

    [Fact]
    public async Task ValidateAsync_BareJwtBody_DoesNotThrow()
    {
        var chain = FakeAppleChain.Create();
        using var authenticator = chain.Authenticator(Options());

        await authenticator.ValidateAsync(chain.Sign(new { data = Data() }));
    }

    [Fact]
    public async Task ValidateAsync_BearerTokenIsIgnoredForApple()
    {
        var chain = FakeAppleChain.Create();
        using var authenticator = chain.Authenticator(Options());

        await authenticator.ValidateAsync(Wrap(chain.Sign(new { data = Data() })), bearerToken: "ignored-token");
    }

    [Fact]
    public async Task ValidateAsync_ChainNotTrustedByRoot_Throws()
    {
        var signer = FakeAppleChain.Create();
        var other = FakeAppleChain.Create();
        using var authenticator = other.Authenticator(Options());

        await Assert.ThrowsAsync<WebhookAuthenticationException>(
            () => authenticator.ValidateAsync(Wrap(signer.Sign(new { data = Data() }))));
    }

    [Fact]
    public async Task ValidateAsync_TamperedPayload_Throws()
    {
        var chain = FakeAppleChain.Create();
        using var authenticator = chain.Authenticator(Options());

        var parts = chain.Sign(new { data = Data() }).Split('.');
        var tampered = $"{parts[0]}.{B64(JsonSerializer.Serialize(new { data = Data(), injected = true }))}.{parts[2]}";

        await Assert.ThrowsAsync<WebhookAuthenticationException>(() => authenticator.ValidateAsync(Wrap(tampered)));
    }

    [Fact]
    public async Task ValidateAsync_LeafWithoutAppleMarkerOid_Throws()
    {
        // A certificate that chains to the trusted root but is not an App Store signing certificate
        // (e.g. some other certificate issued under Apple Root CA - G3) must be rejected.
        var chain = FakeAppleChain.Create(leafOid: false);
        using var authenticator = chain.Authenticator(Options());

        var ex = await Assert.ThrowsAsync<WebhookAuthenticationException>(
            () => authenticator.ValidateAsync(Wrap(chain.Sign(new { data = Data() }))));
        Assert.Contains(LeafOid, ex.Message);
    }

    [Fact]
    public async Task ValidateAsync_IntermediateWithoutWwdrMarkerOid_Throws()
    {
        var chain = FakeAppleChain.Create(intermediateOid: false);
        using var authenticator = chain.Authenticator(Options());

        var ex = await Assert.ThrowsAsync<WebhookAuthenticationException>(
            () => authenticator.ValidateAsync(Wrap(chain.Sign(new { data = Data() }))));
        Assert.Contains(IntermediateOid, ex.Message);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public async Task ValidateAsync_X5cNotThreeCertificates_Throws(int count)
    {
        var chain = FakeAppleChain.Create();
        using var authenticator = chain.Authenticator(Options());

        var x5c = Enumerable.Range(0, count).Select(i => chain.X5c[Math.Min(i, 2)]).ToArray();
        await Assert.ThrowsAsync<WebhookAuthenticationException>(
            () => authenticator.ValidateAsync(Wrap(chain.Sign(new { data = Data() }, x5c: x5c))));
    }

    [Fact]
    public async Task ValidateAsync_MultipleRoots_AcceptsChainToAnyTrustedRoot()
    {
        var chain = FakeAppleChain.Create();
        var unrelated = FakeAppleChain.Create();
        using var authenticator = new AppleWebhookAuthenticator(Options(), [unrelated.RootDer, chain.RootDer]);

        await authenticator.ValidateAsync(Wrap(chain.Sign(new { data = Data() })));
    }

    // ──────────────── app identity ────────────────

    [Fact]
    public async Task ValidateAsync_OtherBundle_Throws()
    {
        // Genuinely Apple-signed, but for somebody else's app
        var chain = FakeAppleChain.Create();
        using var authenticator = chain.Authenticator(Options());

        var ex = await Assert.ThrowsAsync<WebhookAuthenticationException>(
            () => authenticator.ValidateAsync(Wrap(chain.Sign(new { data = Data(bundleId: "com.attacker.app") }))));
        Assert.Contains("com.attacker.app", ex.Message);
    }

    [Fact]
    public async Task ValidateAsync_NoAppIdentity_Throws()
    {
        var chain = FakeAppleChain.Create();
        using var authenticator = chain.Authenticator(Options());

        await Assert.ThrowsAsync<WebhookAuthenticationException>(
            () => authenticator.ValidateAsync(Wrap(chain.Sign(new { notificationType = "TEST" }))));
    }

    [Fact]
    public async Task ValidateAsync_SummaryPayload_UsesSummaryBundle()
    {
        var chain = FakeAppleChain.Create();
        using var authenticator = chain.Authenticator(Options());

        await authenticator.ValidateAsync(Wrap(chain.Sign(new
        {
            notificationType = "RENEWAL_EXTENSION",
            subtype = "SUMMARY",
            summary = new { bundleId = BundleId, environment = "Production", appAppleId = 1234 }
        })));
    }

    [Fact]
    public async Task ValidateAsync_ExternalPurchaseTokenPayload_UsesTokenBundle()
    {
        var chain = FakeAppleChain.Create();
        using var authenticator = chain.Authenticator(Options(o => o.AllowSandbox = false));

        await authenticator.ValidateAsync(Wrap(chain.Sign(new
        {
            notificationType = "EXTERNAL_PURCHASE_TOKEN",
            externalPurchaseToken = new { bundleId = BundleId, appAppleId = 1234, externalPurchaseId = "abc" }
        })));
    }

    [Fact]
    public async Task ValidateAsync_SandboxNotAllowed_Throws()
    {
        var chain = FakeAppleChain.Create();
        using var authenticator = chain.Authenticator(Options(o => o.AllowSandbox = false));

        await Assert.ThrowsAsync<WebhookAuthenticationException>(
            () => authenticator.ValidateAsync(Wrap(chain.Sign(new { data = Data(environment: "Sandbox") }))));
    }

    [Fact]
    public async Task ValidateAsync_SandboxAllowedByDefault()
    {
        var chain = FakeAppleChain.Create();
        using var authenticator = chain.Authenticator(Options(o => o.AppAppleId = 1234));

        // Sandbox notifications carry no appAppleId — must not be checked against it
        await authenticator.ValidateAsync(Wrap(chain.Sign(new { data = Data(environment: "Sandbox", appAppleId: null) })));
    }

    [Fact]
    public async Task ValidateAsync_WrongAppAppleIdInProduction_Throws()
    {
        var chain = FakeAppleChain.Create();
        using var authenticator = chain.Authenticator(Options(o => o.AppAppleId = 1234));

        await Assert.ThrowsAsync<WebhookAuthenticationException>(
            () => authenticator.ValidateAsync(Wrap(chain.Sign(new { data = Data(appAppleId: 9999) }))));
    }

    // ──────────────── nested signed JWTs ────────────────

    [Fact]
    public async Task ValidateAsync_NestedTransactionSignedBySameChain_DoesNotThrow()
    {
        var chain = FakeAppleChain.Create();
        using var authenticator = chain.Authenticator(Options());

        var nested = chain.Sign(new { bundleId = BundleId, environment = "Production", productId = "premium", transactionId = "tx-1" });
        await authenticator.ValidateAsync(Wrap(chain.Sign(new { data = Data(signedTransactionInfo: nested) })));
    }

    [Fact]
    public async Task ValidateAsync_NestedTransactionSignedByDifferentChain_Throws()
    {
        var chain = FakeAppleChain.Create();
        var other = FakeAppleChain.Create();
        using var authenticator = chain.Authenticator(Options());

        var nested = other.Sign(new { bundleId = BundleId, productId = "premium", transactionId = "tx-1" });
        await Assert.ThrowsAsync<WebhookAuthenticationException>(
            () => authenticator.ValidateAsync(Wrap(chain.Sign(new { data = Data(signedTransactionInfo: nested) }))));
    }

    [Fact]
    public async Task ValidateAsync_NestedTransactionForOtherBundle_Throws()
    {
        var chain = FakeAppleChain.Create();
        using var authenticator = chain.Authenticator(Options());

        var nested = chain.Sign(new { bundleId = "com.attacker.app", environment = "Production", transactionId = "tx-1" });
        await Assert.ThrowsAsync<WebhookAuthenticationException>(
            () => authenticator.ValidateAsync(Wrap(chain.Sign(new { data = Data(signedTransactionInfo: nested) }))));
    }

    [Fact]
    public async Task ValidateAsync_NestedTransactionEnvironmentMismatch_Throws()
    {
        var chain = FakeAppleChain.Create();
        using var authenticator = chain.Authenticator(Options());

        var nested = chain.Sign(new { bundleId = BundleId, environment = "Sandbox", transactionId = "tx-1" });
        await Assert.ThrowsAsync<WebhookAuthenticationException>(
            () => authenticator.ValidateAsync(Wrap(chain.Sign(new { data = Data(signedTransactionInfo: nested) }))));
    }

    // ──────────────────────── helpers ────────────────────────

    private static string Wrap(string jwt) => $$"""{"signedPayload":"{{jwt}}"}""";

    private static string B64(string s) => B64(Encoding.UTF8.GetBytes(s));

    private static string B64(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// A root → intermediate → leaf chain shaped like Apple's: the intermediate carries the WWDR marker
    /// OID and the leaf the App Store signing marker OID (each can be omitted to test rejection).
    /// </summary>
    private sealed class FakeAppleChain
    {
        public required byte[] RootDer { get; init; }
        public required string[] X5c { get; init; }
        public required ECDsa LeafKey { get; init; }

        public static FakeAppleChain Create(bool leafOid = true, bool intermediateOid = true)
        {
            var now = DateTimeOffset.UtcNow;

            using var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var rootReq = new CertificateRequest("CN=Fake Apple Root CA - G3", rootKey, HashAlgorithmName.SHA256);
            rootReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            rootReq.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
            using var root = rootReq.CreateSelfSigned(now.AddDays(-2), now.AddYears(3));

            using var intKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var intReq = new CertificateRequest("CN=Fake Apple WWDR CA", intKey, HashAlgorithmName.SHA256);
            intReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
            intReq.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
            if (intermediateOid)
                intReq.CertificateExtensions.Add(new X509Extension(IntermediateOid, [0x05, 0x00], false));
            using var intermediatePublic = intReq.Create(root, now.AddDays(-1), now.AddYears(2), [0x01]);
            using var intermediate = intermediatePublic.CopyWithPrivateKey(intKey);

            var leafKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var leafReq = new CertificateRequest("CN=Fake Prod ECC Mac App Store Signing", leafKey, HashAlgorithmName.SHA256);
            leafReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            if (leafOid)
                leafReq.CertificateExtensions.Add(new X509Extension(LeafOid, [0x05, 0x00], false));
            using var leaf = leafReq.Create(intermediate, now.AddDays(-1), now.AddYears(1), [0x02]);

            return new FakeAppleChain
            {
                RootDer = root.Export(X509ContentType.Cert),
                X5c = new[] { leaf, intermediatePublic, root }.Select(c => Convert.ToBase64String(c.Export(X509ContentType.Cert))).ToArray(),
                LeafKey = leafKey
            };
        }

        public AppleWebhookAuthenticator Authenticator(AppleWebhookAuthenticatorOptions options) =>
            new(options, [RootDer]);

        /// <summary>Signs <paramref name="payload"/> as an ES256 JWT with this chain in the x5c header.</summary>
        public string Sign(object payload, string alg = "ES256", string[]? x5c = null)
        {
            var header = B64(JsonSerializer.Serialize(new { alg, x5c = x5c ?? X5c }));
            var body = B64(JsonSerializer.Serialize(payload));
            var signature = LeafKey.SignData(Encoding.ASCII.GetBytes($"{header}.{body}"),
                HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            return $"{header}.{body}.{B64(signature)}";
        }
    }
}
