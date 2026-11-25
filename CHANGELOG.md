# Changelog

All notable changes to the StoreBridge packages (`StoreBridge`, `StoreBridge.Apple`, `StoreBridge.Android`).
Versions follow [SemVer](https://semver.org/); while the major version is `0`, minor versions may contain breaking changes.

## [0.3.0] — unreleased

> **Upgrade from 0.2.0 as soon as possible.** 0.2.0 contains two bugs that break real-world use (see *Fixed*).

### Fixed
- **Apple — subscription verification with `productId` always failed against the real API.** Apple's
  `lastTransactions` items carry no `productId`; the product is now matched against the decoded
  `signedTransactionInfo`. Without `productId`, the item for the requested transaction ID is preferred.
- **Google — real Real-time Developer Notifications could not be parsed.** Google sends `eventTimeMillis` as a JSON
  string; it is now accepted as string or number. *(Present since 0.1.0.)*
- Apple webhook authenticator and parsers no longer leak `JsonException`, `InvalidOperationException` or
  `CryptographicException` on malformed input — only `WebhookAuthenticationException`, `FormatException` or
  `ArgumentException` (found by fuzzing; such bodies previously produced HTTP 500s and redelivery loops).
- `DecodedRenewalInfo` reads Apple's `expirationIntent` (the previous `expirationReason` field does not exist).
- Out-of-range Unix timestamps raise `FormatException` instead of `ArgumentOutOfRangeException`.
- Transaction IDs `.` / `..` are rejected before building the request URL (path normalisation).
- Google SDK retries are disabled so they no longer multiply with StoreBridge's own retries.

### Added
- `SubscriptionVerificationResult.IsAcknowledged`, `LinkedPurchaseToken` (Google) and `ExternalAccountId`
  (Apple `appAccountToken` / Google `obfuscatedExternalAccountId`).
- `RequestTimeout` (default 30 s) on `AppleApiOptions` and `AndroidOptions`.
- Opt-in `AppleApiOptions.VerifyResponseSignatures`, `AppleWebhookAuthenticatorOptions.ValidateCertificateAtSignedDate`
  and `AppleWebhookAuthenticatorOptions.EnableOnlineRevocationCheck`.
- `AddAndroidWebhooks(IConfiguration)`.
- Apple bearer JWT cache (reused for 15 of its 20 minutes), `Retry-After` support (capped at 30 s) and ±25 % retry jitter.
- Android verifiers implement `IDisposable`; all verifiers throw `ObjectDisposedException` after disposal.
- Tests against a notification genuinely signed by Apple, and seeded fuzz tests of every webhook entry point.

### Changed
- Apple verifiers reject API responses whose decoded `bundleId` differs from the configured `BundleId`.
- Apple ES256 keys and signatures must be P-256 / 64 bytes; oversized JWT headers and `x5c` entries are rejected.
- `AndroidInAppPurchaseWebhookParser` throws `FormatException` for voided **subscriptions** (`productType = 1`);
  they are handled by `AndroidWebhookParser`.
- Apple `DID_CHANGE_RENEWAL_STATUS` with an unknown subtype maps to `Other` instead of `AutoRenewEnabled`.
- DI registrations are idempotent and no longer require logging to be registered.
- `AndroidWebhookAuthenticator` reports an unparsable body as `WebhookAuthenticationException` when
  `ExpectedPackageName` is set.

### Documentation
- The README shown on nuget.org now uses absolute links (the relative `LICENSE`, workflow and sample links were
  broken there) and lists the real dependencies (`StoreBridge.Apple` has not used IdentityModel since 0.2.0).
- Build/release instructions moved to `CONTRIBUTING.md`; package descriptions, tags, copyright and release notes
  improved.

## [0.2.0] — 2026-11-25

### Security
- Apple webhooks: Apple marker OIDs on leaf/intermediate, exactly 3 certificates, chain through the x5c
  intermediate, AIA downloads disabled, and binding to the app (`BundleId` required, `AppAppleId`, `AllowSandbox`).
- Google webhooks: `ExpectedServiceAccountEmail` required, `email_verified` checked, optional `ExpectedPackageName`.
- Verifiers never fall back to a different product than the requested `productId`.
- Google credentials restricted to service-account keys (`CredentialFactory`).

### Added / Changed
- Targets `net10.0` (.NET 8/9 are out of support). Google one-time purchases use `purchases.productsv2`.
- `Cancelled` status means the same in both stores and both paths; `SubscriptionStatus.GrantsAccess()`.
- Apple production → sandbox fallback on 404; new event types (`ONE_TIME_CHARGE`, `REFUND_REVERSED`,
  Google `pendingRefundReviewNotification`, RTDN types 17–22); timeouts/429/OAuth errors become `Failure`.
- Purchase tokens / transaction IDs are masked in logs. NuGet Trusted Publishing.

## [0.1.1] — 2026-05-30
- New NuGet package icon.

## [0.1.0] — 2026-05-30
- Initial release.
