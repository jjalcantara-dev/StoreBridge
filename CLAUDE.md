# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

```bash
# Restore dependencies
dotnet restore

# Build (Release)
dotnet build --configuration Release

# Run all tests
dotnet test --configuration Release

# Run a single test project
dotnet test tests/StoreBridge.Apple.Tests --configuration Release
dotnet test tests/StoreBridge.Android.Tests --configuration Release

# Pack NuGet packages (requires version)
dotnet pack --configuration Release /p:Version=1.0.0 --output ./nupkgs
```

## Architecture

This is a .NET library (targets net10.0 — .NET 8/9 reached end of support on 2026-11-10; add net11.0 once CI uses SDK 11) that provides normalized subscription verification and webhook parsing for both Apple App Store and Google Play Store. It is published as three NuGet packages.

### Package layout

- **`StoreBridge`** — Core abstractions and shared models. No platform-specific dependencies.
- **`StoreBridge.Apple`** — App Store Server API v1 integration, signed JWT generation, and signed notification v2 parsing.
- **`StoreBridge.Android`** — Google Play Developer API v3 integration and Pub/Sub notification parsing.

### Key abstractions (in Core)

- `ISubscriptionVerifier` — Verifies a subscription token against the store's API. Returns `SubscriptionVerificationResult`.
- `IInAppPurchaseVerifier` — Verifies a one-time in-app purchase token. Returns `InAppPurchaseVerificationResult`.
- `IWebhookParser` / `IInAppPurchaseWebhookParser` — Parse raw server-to-server notification payloads into `SubscriptionNotification` / `InAppPurchaseNotification`.
- `IWebhookAuthenticator` — Cryptographically verifies a webhook is genuinely from Apple/Google **and for this app**. **Always call `ValidateAsync` before parsing.**
- `SubscriptionStatus` enum — Normalized across platforms: `Active`, `Expired`, `Cancelled`, `InGracePeriod`, `InBillingRetry`, `Revoked`, `Unknown`. `Cancelled` = auto-renew off but still inside the paid period, used identically by both verifiers and both webhook parsers. `SubscriptionStatusExtensions.GrantsAccess()` is true for `Active`/`Cancelled`/`InGracePeriod`.
- `PurchaseStatus` enum — For one-time purchases: `Purchased`, `Consumed`, `Cancelled`, `Pending`, `Refunded`, `Unknown`.
- `NotificationEventType` enum — Normalized events: `Renewed`, `AutoRenewDisabled`, `AutoRenewEnabled`, `Created`, `InBillingRetry`, `GracePeriod`, `Refunded`, `Cancelled`, `Expired`, `Test`, `Other`, `RefundReversed`. **New enum members are appended after `Other`** so existing numeric values never shift.
- `InAppPurchaseEventType` enum — `Purchased`, `Refunded`, `Cancelled`, `ConsumptionRequest`, `Test`, `Other`, `RefundReversed`, `RefundReviewRequested`. `InAppPurchaseNotification` also carries `OrderId` and `RefundReviewToken` (Google).
- Both notification types expose `NotificationId` — Apple's `notificationUUID` / Google's Pub/Sub `messageId` — use it to deduplicate retried deliveries.
- `PriceConverter` — Static utility; Apple prices are in thousandths, Google prices are in micros.

### Dependency injection

Each platform package ships `IServiceCollection` extensions (`AppleServiceCollectionExtensions`, `AndroidServiceCollectionExtensions`):
`AddAppleSubscriptions`, `AddAppleInAppPurchases`, `AddAppleWebhooks` (requires `AppleWebhookAuthenticatorOptions.BundleId`), and the Android equivalents. Each accepts either an
`Action<TOptions>` or an `IConfiguration` section (`AddAndroidWebhooks` takes only an `Action`). Apple verifiers register a named `HttpClient` via `IHttpClientFactory`
(`AppleSubscriptionVerifier.HttpClientName` / `AppleInAppPurchaseVerifier.HttpClientName`). All verifiers and parsers accept an
optional `ILogger<T>`; when constructed manually without one they fall back to `NullLogger`.

Every DI extension also registers an `IValidateOptions<T>` (internal: `AppleSubscriptionOptionsValidator`,
`AppleInAppPurchaseOptionsValidator`, `AppleWebhookAuthenticatorOptionsValidator`, `AndroidSubscriptionOptionsValidator`,
`AndroidInAppPurchaseOptionsValidator`, `AndroidWebhookAuthenticatorOptionsValidator`). The first time `IOptions<T>.Value` is read, the validator runs full checks
(required fields set, `PrivateKeyBase64` decodes as PKCS#8 ECDSA, `CredentialsBase64` parses as a service-account key through
`CredentialFactory.FromStream<ServiceAccountCredential>`, `WebhookUrl` / sandbox URLs are absolute http(s) URIs, webhook `BundleId` and
`ExpectedServiceAccountEmail` are set, `MaxRetries >= 1`). A failed check raises `OptionsValidationException` with a
human-readable message — by design this surfaces at the first verifier/authenticator resolve (so DI smoke tests catch
misconfigurations in CI), not at the first webhook hit in production.

### Exception handling strategy

The library follows a deliberate, two-tier policy that the unit tests pin down — keep it consistent when adding code:

1. **Configuration errors fail fast.** `IValidateOptions<T>` runs on first resolve; manual construction without DI is
   protected by `AppleJwtHelper.GenerateToken` (which wraps `FormatException` and `CryptographicException` from the
   `.p8` import into `InvalidOperationException` with a clear remediation message). Either way the user gets a
   specific, fixable error, not a generic stack trace.
2. **Transient/external errors become `Failure(...)` results.** Both verifiers catch:
   - `HttpRequestException` / `Google.GoogleApiException` after retries are exhausted
   - `TimeoutException` (the retry helpers convert an HTTP timeout — `TaskCanceledException` while the caller's token
     is *not* cancelled — into this after retries)
   - `JsonException` / `FormatException` if the upstream API returns a malformed body
   - `InvalidOperationException` / `FormatException` from `AppleJwtHelper` or `AndroidPublisherFactory` (bad credentials),
     and Google `TokenResponseException` (OAuth exchange failed, e.g. deleted key). The Google client `Lazy<>` uses
     `PublicationOnly` so a credential failure is not cached forever.
   - Empty `purchaseToken` / `transactionId` arguments
   - A `productId` that does not match the purchase → `Failure` (never fall back to another product)
   - Apple-style 410 Gone on Google → `IsVerified = true, Status = Expired` (correct, not an error)

   `OperationCanceledException` caused by the **caller's** token **always propagates** — never swallow it.
   Each verifier calls `cancellationToken.ThrowIfCancellationRequested()` on entry so callers see cancellation even
   if the underlying HTTP client doesn't honor the token.

Retry: `AppleRetryHelper` and `AndroidRetryHelper` both use fibonacci backoff (1, 2, 3, 5, 8 s) and retry on 5xx, 429,
no-status network failures and HTTP timeouts. Other 4xx propagate immediately — permanent caller errors. Default
`MaxRetries = 3` on both `AppleApiOptions` and `AndroidOptions`.

### Apple implementation

`AppleSubscriptionVerifier` and `AppleInAppPurchaseVerifier` call the App Store Server API using a short-lived JWT (ES256) built by hand in `AppleJwtHelper` (no IdentityModel dependency) from a private key provided as base64 in options. The JWT is generated once per call and shared across retry attempts. Both verifiers go through `AppleApiClient.GetAsync` (bearer auth, URL-escaped identifier, retries, error mapping).

Sandbox vs production is controlled by `AppleSubscriptionOptions.SubscriptionsBaseUrl` (or `AppleInAppPurchaseOptions.TransactionsBaseUrl`). The default is production. With `SandboxFallback = true` (default), a `404` from the primary URL is retried once against `SandboxSubscriptionsBaseUrl` / `SandboxTransactionsBaseUrl` — App Review / TestFlight purchases are sandbox transactions. `IsSandbox` is true for any environment other than `Production` (`Sandbox`, `Xcode`, `LocalTesting`).

`AppleWebhookParser` / `AppleInAppPurchaseWebhookParser` decode nested signed JWTs from App Store Server Notifications v2 — they only decode payloads, they do **not** verify signatures. `AppleWebhookAuthenticator` is the security boundary and mirrors Apple's App Store Server Libraries: `alg` must be ES256, `x5c` must hold exactly 3 certs, the leaf must carry OID `1.2.840.113635.100.6.11.1` and the intermediate OID `1.2.840.113635.100.6.2.1`, the chain must be leaf → x5c intermediate → a pinned root (bundled Apple Root CA - G3; AIA downloads disabled), and the ES256 signature must verify — for the outer JWT and the nested `signedTransactionInfo` / `signedRenewalInfo`. It then binds the notification to the app via `AppleWebhookAuthenticatorOptions`: `bundleId` (from `data`, `summary` or `externalPurchaseToken`, and the nested transaction) must match `BundleId`, `AppAppleId` is checked on production notifications when set, and non-production environments are rejected when `AllowSandbox = false`. Call `AppleWebhookAuthenticator.ValidateAsync` before handing the body to a parser. A second constructor accepts one or many DER-encoded root CAs (root rotation, tests). Event mapping follows Apple's docs: `DID_FAIL_TO_RENEW` without subtype → `InBillingRetry`; `ONE_TIME_CHARGE` → `Purchased`; `REFUND_REVERSED` → `RefundReversed`. The `TEST` notification type (triggered via the App Store Server API `requestTestNotification` endpoint) maps to `NotificationEventType.Test` / `InAppPurchaseEventType.Test`.

Internal helpers live under `StoreBridge.Apple/Internal/`: `AppleJwtHelper.cs` (JWT generation/decoding, wraps bad-key errors into `InvalidOperationException`), `AppleApiModels.cs` (deserialization models), `AppleRetryHelper.cs` (retry loop), `AppleStatusMapper.cs` (numeric status → `SubscriptionStatus` mapping), `AppleCertificateChainValidator.cs` (x5c chain + signature validation), and `AppleOptionsValidator.cs` (`IValidateOptions<T>` implementations).

### Android implementation

`AndroidSubscriptionVerifier` (Subscriptionsv2 API) and `AndroidInAppPurchaseVerifier` (Productsv2 API — `getproductpurchasev2`) lazily initialize their Google API clients from base64-encoded service account credentials JSON via `AndroidPublisherFactory`. An HTTP 410 Gone response is treated as the purchase no longer being valid.

Both verifiers use thin internal interfaces (`IAndroidSubscriptionsv2`, `IAndroidProducts`) that wrap the concrete Google client. This allows unit testing with NSubstitute without mocking the Google SDK directly. Both verifiers expose an internal constructor that accepts these interfaces.

`AndroidWebhookParser` decodes the base64-encoded JSON message inside the Pub/Sub wrapper and handles `subscriptionNotification` (types 1–13 and 17–22), `voidedPurchaseNotification` with `productType = 1` (subscription → `Refunded`/`Revoked`), and `testNotification` (mapped to `NotificationEventType.Test`); anything else throws `FormatException`. `AndroidInAppPurchaseWebhookParser` handles `oneTimeProductNotification`, `voidedPurchaseNotification` (refunds/chargebacks → `InAppPurchaseEventType.Refunded`, `orderId` in `OrderId`), `pendingRefundReviewNotification` (→ `RefundReviewRequested`, token in `RefundReviewToken`), and `testNotification`. `AndroidPubSubReader` wraps `JsonException` into `FormatException`. Google no longer documents `subscriptionNotification.subscriptionId`, so `SubscriptionNotification.ProductId` may be empty for Android. `AndroidPubSubReader.Parse` returns both the decoded `DeveloperNotification` and the Pub/Sub envelope `messageId` (surfaced as `NotificationId`).

`AndroidWebhookAuthenticator` validates the OIDC Bearer token Google attaches to Pub/Sub push requests (signature, audience = your webhook URL) and **requires** `ExpectedServiceAccountEmail` to match the token's verified `email` — audience alone is forgeable by any GCP account. Optional `ExpectedPackageName` checks the notification's `packageName`. Call `ValidateAsync` with the `Authorization` header value before parsing.

`AndroidSubscriptionVerifier` maps `subscriptionState` first and only lets a past `expiryTime` downgrade `ACTIVE`/`CANCELED` to `Expired` (in `ON_HOLD`/`PAUSED` the expiry is legitimately in the past). `CANCELED` → `Cancelled`; `CancelledByUser` is true only for `userInitiatedCancellation`.

Internal helpers: `AndroidPublisherFactory.cs`, `AndroidPubSubModels.cs`, `AndroidPubSubReader.cs`, `IAndroidSubscriptionsv2.cs`, `IAndroidProducts.cs`, `GoogleTokenValidator.cs`, `IGoogleTokenValidator.cs` (test seam for the OIDC validator), `AndroidRetryHelper.cs` (fibonacci-spaced retries for transient `GoogleApiException`/`HttpRequestException`), and `AndroidOptionsValidator.cs` (`IValidateOptions<T>` implementations).

Android subscription prices come from `lineItems[].autoRenewingPlan.recurringPrice` (list price in micros; excludes taxes and offer discounts; empty for prepaid plans). The voided-purchase notification does not carry the SKU; its `orderId` goes to `OrderId` and `ProductId` stays empty.

Credentials are loaded with `CredentialFactory.FromStream<ServiceAccountCredential>(...).ToGoogleCredential()` (sync API; replaces the deprecated `GoogleCredential.FromStream`, which also accepted `external_account` configs that can point at arbitrary files/URLs). One-time purchases use `purchases.productsv2.getproductpurchasev2`: token-only call; an optional `productId` must match a `productLineItem` (else `Failure`); `PURCHASED` + `refundableQuantity == 0` → `Refunded`, consumption state `CONSUMED` → `Consumed`; `OrderId` and `RefundableQuantity` are surfaced; `testPurchaseContext` → `IsSandbox`. Acknowledge/consume (still v1 `purchases.products`) are not wrapped.

### Logging

Never log raw purchase tokens or transaction IDs: wrap them with `LogRedaction.Token(...)` (`src/Shared/LogRedaction.cs`,
namespace `StoreBridge.Internal`, compiled into both platform packages through a linked `<Compile Include>`).

### Testing

All tests are unit tests — no integration tests against real Apple/Google APIs.

Test projects use **xunit.v3** via the `xunit.v3.mtp-off` package (keeps the VSTest pipeline: `coverlet.collector`, `--logger trx`); `xUnit1051` is suppressed.

**Apple tests** use `FakeHttpMessageHandler` (queued responses or exceptions, call count, request URIs, last bearer token) to simulate HTTP responses. Fake unsigned JWTs are constructed manually for `signedTransactionInfo` and `signedRenewalInfo` payloads in verifier/parser tests. Authenticator tests build a real root → intermediate (WWDR OID) → leaf (App Store OID) chain with `CertificateRequest` (`FakeAppleChain`), with switches to omit each OID.

**Android tests** use NSubstitute to mock `IAndroidSubscriptionsv2`, `IAndroidProducts`, and `IGoogleTokenValidator`. All three interfaces are `internal`; `InternalsVisibleTo("StoreBridge.Android.Tests")` and `InternalsVisibleTo("DynamicProxyGenAssembly2")` are set in the Android project file to allow the test assembly to reference the interfaces and to let NSubstitute create proxies.

A separate **sample** project under `samples/StoreBridge.Sample.Web/` is a minimal ASP.NET Core host that doubles as a real-payload validation harness. It is not packed (`IsPackable=false`) and is intended for manual smoke tests against real Apple/Google test notifications and sandbox transactions.

## Publishing

Releases are triggered by pushing a semver git tag. GitHub Actions (`.github/workflows/publish.yml`) extracts the version from the tag, builds with `-p:Version=<tag>` (so assembly versions match), packs all three projects, and pushes to NuGet.org via **Trusted Publishing** (`NuGet/login@v1` exchanges the job's OIDC token — `permissions: id-token: write` — for a short-lived API key). Needs a Trusted Publishing policy on nuget.org for `publish.yml` and the `NUGET_USER` secret (nuget.org profile name).

```bash
git tag v1.0.0
git push origin v1.0.0
```
