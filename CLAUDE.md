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

This is a .NET library (multi-targeted: net8.0, net9.0, net10.0) that provides normalized subscription verification and webhook parsing for both Apple App Store and Google Play Store. It is published as three NuGet packages.

### Package layout

- **`StoreBridge`** — Core abstractions and shared models. No platform-specific dependencies.
- **`StoreBridge.Apple`** — App Store Server API v1 integration, signed JWT generation, and signed notification v2 parsing.
- **`StoreBridge.Android`** — Google Play Developer API v3 integration and Pub/Sub notification parsing.

### Key abstractions (in Core)

- `ISubscriptionVerifier` — Verifies a subscription token against the store's API. Returns `SubscriptionVerificationResult`.
- `IInAppPurchaseVerifier` — Verifies a one-time in-app purchase token. Returns `InAppPurchaseVerificationResult`.
- `IWebhookParser` / `IInAppPurchaseWebhookParser` — Parse raw server-to-server notification payloads into `SubscriptionNotification` / `InAppPurchaseNotification`.
- `IWebhookAuthenticator` — Cryptographically verifies a webhook is genuinely from Apple/Google. **Always call `ValidateAsync` before parsing.**
- `SubscriptionStatus` enum — Normalized across platforms: `Active`, `Expired`, `Cancelled`, `InGracePeriod`, `InBillingRetry`, `Revoked`, `Unknown`.
- `PurchaseStatus` enum — For one-time purchases: `Purchased`, `Consumed`, `Cancelled`, `Pending`, `Refunded`, `Unknown`.
- `NotificationEventType` enum — Normalized events: `Renewed`, `AutoRenewDisabled`, `AutoRenewEnabled`, `Created`, `InBillingRetry`, `GracePeriod`, `Refunded`, `Cancelled`, `Expired`, `Test`, `Other`.
- `InAppPurchaseEventType` enum — `Purchased`, `Refunded`, `Cancelled`, `ConsumptionRequest`, `Test`, `Other`.
- Both notification types expose `NotificationId` — Apple's `notificationUUID` / Google's Pub/Sub `messageId` — use it to deduplicate retried deliveries.
- `PriceConverter` — Static utility; Apple prices are in thousandths, Google prices are in micros.

### Dependency injection

Each platform package ships `IServiceCollection` extensions (`AppleServiceCollectionExtensions`, `AndroidServiceCollectionExtensions`):
`AddAppleSubscriptions`, `AddAppleInAppPurchases`, `AddAppleWebhooks`, and the Android equivalents. Each accepts either an
`Action<TOptions>` or an `IConfiguration` section. Apple verifiers register a named `HttpClient` via `IHttpClientFactory`
(`AppleSubscriptionVerifier.HttpClientName` / `AppleInAppPurchaseVerifier.HttpClientName`). All verifiers and parsers accept an
optional `ILogger<T>`; when constructed manually without one they fall back to `NullLogger`.

Every DI extension also registers an `IValidateOptions<T>` (internal: `AppleSubscriptionOptionsValidator`,
`AppleInAppPurchaseOptionsValidator`, `AndroidSubscriptionOptionsValidator`, `AndroidInAppPurchaseOptionsValidator`,
`AndroidWebhookAuthenticatorOptionsValidator`). The first time `IOptions<T>.Value` is read, the validator runs full checks
(required fields set, `PrivateKeyBase64` decodes as PKCS#8 ECDSA, `CredentialsBase64` parses through `GoogleCredential.FromStream`,
`WebhookUrl` is an absolute http(s) URI, `MaxRetries >= 1`). A failed check raises `OptionsValidationException` with a
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
   - `JsonException` if the upstream API returns a malformed body
   - `InvalidOperationException` from `AppleJwtHelper` or `GoogleCredential.FromStream` (bad credentials)
   - Empty `purchaseToken` / `transactionId` arguments
   - Apple-style 410 Gone on Google → `IsVerified = true, Status = Expired` (correct, not an error)

   `OperationCanceledException` (and the derived `TaskCanceledException`) **always propagates** — never swallow it.
   Each verifier calls `cancellationToken.ThrowIfCancellationRequested()` on entry so callers see cancellation even
   if the underlying HTTP client doesn't honor the token.

Retry: `AppleRetryHelper` and `AndroidRetryHelper` both use fibonacci backoff (1, 2, 3, 5, 8 s). Apple retries on 5xx
and on `HttpRequestException` with no status code (network failures). Android retries on `GoogleApiException` with
5xx, 429 (Too Many Requests), or no status code, plus raw `HttpRequestException`. 4xx (other than 429) propagates
immediately — it's a permanent caller error. Default `MaxRetries = 3` on both `AppleApiOptions` and `AndroidOptions`.

### Apple implementation

`AppleSubscriptionVerifier` and `AppleInAppPurchaseVerifier` call the App Store Server API using a short-lived JWT (ES256) generated from a private key provided as base64 in options. The JWT is generated once per call and shared across retry attempts.

Retry logic lives in `AppleRetryHelper` (fibonacci delays: 1, 2, 3, 5, 8 s). Only 5xx responses and network errors (`HttpRequestException` with no status code) are retried — 4xx responses propagate immediately without retrying. Default `MaxRetries` is 3.

Sandbox vs production is controlled by `AppleSubscriptionOptions.SubscriptionsBaseUrl` (or `AppleInAppPurchaseOptions.TransactionsBaseUrl`). The default is production.

`AppleWebhookParser` / `AppleInAppPurchaseWebhookParser` decode nested signed JWTs from App Store Server Notifications v2 — they only decode payloads, they do **not** verify signatures. `AppleWebhookAuthenticator` is the security boundary: it validates the x5c certificate chain against the bundled Apple Root CA - G3 and verifies the ES256 signature of the outer signed payload JWT, then re-runs the same validation against any nested `signedTransactionInfo` / `signedRenewalInfo` JWTs. Call `AppleWebhookAuthenticator.ValidateAsync` before handing the body to a parser. The authenticator also exposes overloads that accept one or many DER-encoded root CAs, so you can pin a custom root or trust both old and new Apple roots during a rotation. The `TEST` notification type (triggered via the App Store Server API `requestTestNotification` endpoint) maps to `NotificationEventType.Test` / `InAppPurchaseEventType.Test`.

Internal helpers live under `StoreBridge.Apple/Internal/`: `AppleJwtHelper.cs` (JWT generation/decoding, wraps bad-key errors into `InvalidOperationException`), `AppleApiModels.cs` (deserialization models), `AppleRetryHelper.cs` (retry loop), `AppleStatusMapper.cs` (numeric status → `SubscriptionStatus` mapping), `AppleCertificateChainValidator.cs` (x5c chain + signature validation), and `AppleOptionsValidator.cs` (`IValidateOptions<T>` implementations).

### Android implementation

`AndroidSubscriptionVerifier` (Subscriptionsv2 API) and `AndroidInAppPurchaseVerifier` (Products API) lazily initialize their Google API clients from base64-encoded service account credentials JSON via `AndroidPublisherFactory`. An HTTP 410 Gone response is treated as the purchase no longer being valid.

Both verifiers use thin internal interfaces (`IAndroidSubscriptionsv2`, `IAndroidProducts`) that wrap the concrete Google client. This allows unit testing with NSubstitute without mocking the Google SDK directly. Both verifiers expose an internal constructor that accepts these interfaces.

`AndroidWebhookParser` decodes the base64-encoded JSON message inside the Pub/Sub wrapper and handles `subscriptionNotification` plus `testNotification` (mapped to `NotificationEventType.Test`); a payload with only `oneTimeProductNotification` throws `FormatException`. `AndroidInAppPurchaseWebhookParser` handles `oneTimeProductNotification`, `voidedPurchaseNotification` (refunds/chargebacks → `InAppPurchaseEventType.Refunded`), and `testNotification`. `AndroidPubSubReader.Parse` returns both the decoded `DeveloperNotification` and the Pub/Sub envelope `messageId` (surfaced as `NotificationId`).

`AndroidWebhookAuthenticator` validates the OIDC Bearer token Google attaches to Pub/Sub push requests (signature, audience = your webhook URL, optional service-account email). Call `ValidateAsync` with the `Authorization` header value before parsing.

Internal helpers: `AndroidPublisherFactory.cs`, `AndroidPubSubModels.cs`, `AndroidPubSubReader.cs`, `IAndroidSubscriptionsv2.cs`, `IAndroidProducts.cs`, `GoogleTokenValidator.cs`, `IGoogleTokenValidator.cs` (test seam for the OIDC validator), `AndroidRetryHelper.cs` (fibonacci-spaced retries for transient `GoogleApiException`/`HttpRequestException`), and `AndroidOptionsValidator.cs` (`IValidateOptions<T>` implementations).

The Google Play subscriptions v2 endpoint does not return price information in the purchase response, so `SubscriptionVerificationResult.PriceAmount` / `PriceDecimal` / `CurrencyCode` are returned empty for Android subscriptions — query the Play catalog separately if you need price. The voided-purchase notification likewise does not carry the SKU; `AndroidInAppPurchaseWebhookParser` surfaces the `orderId` in `ProductId` for those events.

`GoogleCredential.FromStream()` is used in `AndroidPublisherFactory` despite CS0618 deprecation. The replacement (`CredentialFactory`) is async-only and not suitable here. Warning is suppressed with `#pragma warning disable CS0618`.

### Testing

All tests are unit tests — no integration tests against real Apple/Google APIs.

**Apple tests** use `FakeHttpMessageHandler` (queued responses, call count tracking) to simulate HTTP responses. Fake unsigned JWTs are constructed manually for `signedTransactionInfo` and `signedRenewalInfo` payloads (signature is not verified).

**Android tests** use NSubstitute to mock `IAndroidSubscriptionsv2`, `IAndroidProducts`, and `IGoogleTokenValidator`. All three interfaces are `internal`; `InternalsVisibleTo("StoreBridge.Android.Tests")` and `InternalsVisibleTo("DynamicProxyGenAssembly2")` are set in the Android project file to allow the test assembly to reference the interfaces and to let NSubstitute create proxies.

A separate **sample** project under `samples/StoreBridge.Sample.Web/` is a minimal ASP.NET Core host that doubles as a real-payload validation harness. It is not packed (`IsPackable=false`) and is intended for manual smoke tests against real Apple/Google test notifications and sandbox transactions.

## Publishing

Releases are triggered by pushing a semver git tag. GitHub Actions (`.github/workflows/publish.yml`) extracts the version from the tag, packs all three projects, and pushes to NuGet.org using the `NUGET_API_KEY` secret. No manual intervention is required.

```bash
git tag v1.0.0
git push origin v1.0.0
```
