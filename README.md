# StoreBridge

**One .NET library for verifying and handling Apple App Store and Google Play subscriptions and in-app purchases.** StoreBridge normalizes the two stores' very different APIs and webhook formats behind one set of interfaces, so you can write your billing logic once and run it against both. Targets `net10.0` (the current LTS; .NET 8 and 9 reached end of support on 10 November 2026). Drops cleanly into ASP.NET Core APIs, Azure / AWS Functions, background workers, or plain console apps.

> **Status:** all unit tests pass. The library does the cryptographic heavy lifting (Apple x5c chain validation against Apple Root CA - G3 with Apple's marker-OID checks plus bundle/environment binding, Google OIDC token validation for Pub/Sub) so you don't have to roll your own.

---

## Table of contents

1. [What you get](#what-you-get)
2. [Packages](#packages)
3. [Install](#install)
4. [Quick start — ASP.NET Core](#quick-start--aspnet-core)
5. [Apple step-by-step setup](#apple-step-by-step-setup)
6. [Google Play step-by-step setup](#google-play-step-by-step-setup)
7. [Webhook authentication (security boundary)](#webhook-authentication-security-boundary)
8. [Verifying purchases](#verifying-purchases)
9. [Handling webhooks](#handling-webhooks)
10. [Normalized models reference](#normalized-models-reference)
11. [Robust exception handling](#robust-exception-handling)
12. [Sample web app](#sample-web-app)
13. [Changelog and contributing](#changelog-and-contributing)
14. [Trademarks](#trademarks)
15. [License](#license)

---

## What you get

| Capability | StoreBridge | Hand-rolling it |
|---|---|---|
| Verify Apple subscription / one-time IAP via App Store Server API v1 | ✅ | Generate ES256 JWT, sign with `.p8` key, call API, parse nested signed JWTs |
| Verify Google Play subscription / one-time IAP via Android Publisher v3 | ✅ | Wire up service account, build OAuth client, call subscriptionsv2 / products endpoints |
| Cryptographically authenticate Apple Server Notifications v2 | ✅ | Walk x5c chain, anchor to Apple Root CA - G3, check Apple's marker OIDs, verify ES256 over JWT and nested JWTs, bind to your bundle ID |
| Cryptographically authenticate Google Pub/Sub push webhooks | ✅ | Validate OIDC bearer token (signature, audience, issuer, expiry, service-account email) |
| Normalized status / event enums across both stores | ✅ | Maintain two separate switch statements forever |
| Retry on transient failures with sensible defaults | ✅ (1/2/3/5/8s, both stores) | Roll your own Polly policies |
| **Fail-fast on misconfiguration** | ✅ `IValidateOptions<T>` validates `.p8` import and service-account JSON at startup | Surfaces as cryptic errors during the first real webhook |
| **Robust runtime exception handling** | ✅ HTTP, JSON, credential, cancellation paths all covered | Easy to leak a `JsonException` or `CryptographicException` out to the response |
| Deduplicate webhook retries | ✅ via `NotificationId` | Track Apple `notificationUUID` + Google `messageId` yourself |
| Sandbox vs production | ✅ production by default, automatic sandbox fallback on 404 (App Review / TestFlight) | Track two endpoints per platform |

If you only need one store today, install only that package — there is no transitive dependency on the other.

---

## Packages

| Package | What's in it | NuGet |
|---|---|---|
| `StoreBridge` | Core abstractions and shared models (`ISubscriptionVerifier`, `IInAppPurchaseVerifier`, `IWebhookParser`, `IWebhookAuthenticator`, all DTOs and enums). No platform deps. | [StoreBridge](https://www.nuget.org/packages/StoreBridge) |
| `StoreBridge.Apple` | App Store Server API v1 + Server Notifications v2. Pulls in `StoreBridge` and `Microsoft.Extensions.*` abstractions only — JWTs are signed and verified with `System.Security.Cryptography`, no JWT library. | [StoreBridge.Apple](https://www.nuget.org/packages/StoreBridge.Apple) |
| `StoreBridge.Android` | Google Play Developer API v3 + Pub/Sub. Pulls in `StoreBridge`, `Google.Apis.AndroidPublisher.v3` and `Microsoft.Extensions.*` abstractions. | [StoreBridge.Android](https://www.nuget.org/packages/StoreBridge.Android) |

All packages target `net10.0` and ship XML docs, symbols (`.snupkg`) and Source Link metadata so you can step into the source from a debugger.

---

## Install

Install only what you need:

```bash
# Apple only
dotnet add package StoreBridge.Apple

# Google only
dotnet add package StoreBridge.Android

# Both (each pulls StoreBridge transitively — you don't add it directly)
dotnet add package StoreBridge.Apple
dotnet add package StoreBridge.Android
```

---

## Quick start — ASP.NET Core

```csharp
// Program.cs
var builder = WebApplication.CreateBuilder(args);

// Apple — verifiers + webhook parser + authenticator
builder.Services.AddAppleSubscriptions(opts =>
{
    opts.KeyId            = "ABC1234567";
    opts.IssuerId         = "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx";
    opts.BundleId         = "com.example.app";
    opts.PrivateKeyBase64 = "<base64 of the .p8 key bytes>";
});
builder.Services.AddAppleInAppPurchases(opts => { /* same fields */ });
builder.Services.AddAppleWebhooks(opts =>
{
    opts.BundleId   = "com.example.app"; // required — Apple-signed notifications for other apps are rejected
    opts.AppAppleId = 1234567890;        // optional — checked on production notifications
});

// Google — verifiers + webhook parser + authenticator
builder.Services.AddAndroidSubscriptions(builder.Configuration.GetSection("GooglePlay"));
builder.Services.AddAndroidInAppPurchases(builder.Configuration.GetSection("GooglePlay"));
builder.Services.AddAndroidWebhooks(opts =>
{
    opts.WebhookUrl                  = "https://api.example.com/webhooks/google";
    opts.ExpectedServiceAccountEmail = "pubsub@my-project.iam.gserviceaccount.com";
});

var app = builder.Build();

app.MapPost("/verify/apple", async (AppleSubscriptionVerifier v, VerifyRequest req) =>
{
    var r = await v.VerifySubscriptionAsync(req.OriginalTransactionId, req.ProductId);
    return r.IsVerified ? Results.Ok(r) : Results.UnprocessableEntity(r.ErrorMessage);
});

app.Run();
record VerifyRequest(string OriginalTransactionId, string ProductId);
```

Everything is registered as a **singleton**. Inject any of these into your controllers, minimal APIs, or background services:

- `ISubscriptionVerifier` / `AppleSubscriptionVerifier` / `AndroidSubscriptionVerifier`
- `IInAppPurchaseVerifier` / `AppleInAppPurchaseVerifier` / `AndroidInAppPurchaseVerifier`
- `IWebhookParser` / `AppleWebhookParser` / `AndroidWebhookParser`
- `IInAppPurchaseWebhookParser` / `AppleInAppPurchaseWebhookParser` / `AndroidInAppPurchaseWebhookParser`
- `IWebhookAuthenticator` / `AppleWebhookAuthenticator` / `AndroidWebhookAuthenticator`

Apple verifiers use named `HttpClient` instances through `IHttpClientFactory`
(`AppleSubscriptionVerifier.HttpClientName`, `AppleInAppPurchaseVerifier.HttpClientName`) — you can attach Polly handlers, custom headers, or proxy settings the standard way.

All types accept an optional `ILogger<T>`. DI wires it automatically; manual construction without a logger falls back to `NullLogger`.

---

## Apple step-by-step setup

You need four values, all from **App Store Connect**.

### 1. Create an In-App Purchase API key

1. Sign in to <https://appstoreconnect.apple.com>.
2. Go to **Users and Access → Integrations → In-App Purchase**.
3. Click **Generate API Key** (or **+**). Give it a name, click **Generate**.
4. **Download the `.p8` file immediately — it's only available once.**
5. Copy these values from the same screen:
   - **Key ID** → goes into `KeyId` (e.g. `ABC1234567`)
   - **Issuer ID** → goes into `IssuerId` (a UUID, your team's identifier)

### 2. Base64-encode the private key

The `.p8` file is a PEM file. StoreBridge expects either the raw key bytes (PKCS#8 DER) or the same bytes base64-encoded. The simplest approach:

```bash
# macOS / Linux — strip the PEM header/footer and base64-encode the body
cat AuthKey_ABC1234567.p8 \
  | sed '/-----BEGIN PRIVATE KEY-----/d;/-----END PRIVATE KEY-----/d' \
  | tr -d '\n'
```

```powershell
# Windows PowerShell
(Get-Content AuthKey_ABC1234567.p8 -Raw) `
  -replace '-----BEGIN PRIVATE KEY-----','' `
  -replace '-----END PRIVATE KEY-----','' `
  -replace '\s',''
```

The output is what you pass as `PrivateKeyBase64`. Store it in a secret manager (Azure Key Vault, AWS Secrets Manager, `dotnet user-secrets` for local dev) — **never commit it**.

### 3. Find your bundle ID

It's on every page of your app in App Store Connect — e.g. `com.example.app`. Goes into `BundleId`.

### 4. Configure App Store Server Notifications v2

1. In App Store Connect → your app → **App Information**, scroll to **App Store Server Notifications**.
2. Set the **Production Server URL** and **Sandbox Server URL** to your webhook endpoint (e.g. `https://api.example.com/webhooks/apple`).
3. Set **Version** to **Version 2** (v1 is not supported by this library).
4. Save.
5. To test the wiring, call the App Store Server API `requestTestNotification` endpoint — Apple delivers a real signed `TEST` notification to your URL, which StoreBridge surfaces as `NotificationEventType.Test`.

### 5. Done — wire it up

```csharp
builder.Services.AddAppleSubscriptions(opts =>
{
    opts.KeyId            = configuration["Apple:KeyId"]!;
    opts.IssuerId         = configuration["Apple:IssuerId"]!;
    opts.BundleId         = configuration["Apple:BundleId"]!;
    opts.PrivateKeyBase64 = configuration["Apple:PrivateKeyBase64"]!;

    // Optional: point at the sandbox endpoint while you test
    // opts.SubscriptionsBaseUrl = "https://api.storekit-sandbox.itunes.apple.com/inApps/v1/subscriptions/";

    // Optional: attempts for 5xx / 429 / network failures / timeouts (default 3)
    // opts.MaxRetries = 3;

    // Optional: disable the production → sandbox retry on 404 (default true)
    // opts.SandboxFallback = false;

    // Optional: per-attempt HTTP timeout (default 30 s)
    // opts.RequestTimeout = TimeSpan.FromSeconds(30);

    // Optional: also verify Apple's x5c chain + ES256 signature of the JWS in API responses
    // (defence in depth on top of TLS; default false)
    // opts.VerifyResponseSignatures = true;
});
builder.Services.AddAppleInAppPurchases(opts => { /* same four fields */ });
builder.Services.AddAppleWebhooks(opts =>
{
    opts.BundleId   = configuration["Apple:BundleId"]!;
    opts.AppAppleId = long.Parse(configuration["Apple:AppAppleId"]!); // App Store Connect → App Information → Apple ID

    // Optional (default false): evaluate certificate validity at each JWS's signedDate, like Apple's own
    // libraries do — keeps retried / history notifications verifiable after Apple rotates its leaf certificate
    // opts.ValidateCertificateAtSignedDate = true;

    // Optional (default false): online OCSP/CRL revocation check of Apple's signing chain
    // opts.EnableOnlineRevocationCheck = true;
});
```

The Apple verifiers cache the signed App Store Server API bearer token (valid 20 minutes) and reuse it for 15 minutes, so a busy server doesn't sign a new JWT per request.

### Sandbox vs Production for Apple

Production is the default. Because **App Review and TestFlight purchases are sandbox transactions** even when your
server talks to production, a production lookup that returns `404` is retried once against the sandbox endpoint
(`SandboxSubscriptionsBaseUrl` / `SandboxTransactionsBaseUrl`) — this is Apple's recommended pattern. Turn it off
with `SandboxFallback = false`. To talk only to the sandbox, swap the base URL — everything else is identical:

```csharp
// Sandbox
opts.SubscriptionsBaseUrl = "https://api.storekit-sandbox.itunes.apple.com/inApps/v1/subscriptions/";
opts.TransactionsBaseUrl  = "https://api.storekit-sandbox.itunes.apple.com/inApps/v1/transactions/";

// Production (the defaults)
opts.SubscriptionsBaseUrl = "https://api.storekit.itunes.apple.com/inApps/v1/subscriptions/";
opts.TransactionsBaseUrl  = "https://api.storekit.itunes.apple.com/inApps/v1/transactions/";
```

`SubscriptionVerificationResult.IsSandbox` tells you which environment the *transaction* actually came from, regardless of which endpoint you hit.

> **Sandbox transactions verify successfully.** With the fallback on, a TestFlight / App Review purchase returns `IsVerified = true` and `IsSandbox = true`. Unless you intentionally grant sandbox access in production, check `!result.IsSandbox` before granting paid entitlements.

---

## Google Play step-by-step setup

You need two values: a **service account JSON key** and your **package name**.

### 1. Create a Google Cloud project (or use an existing one)

1. Go to <https://console.cloud.google.com>.
2. Pick or create the project that owns the Pub/Sub topic you'll use for webhooks.

### 2. Enable the Android Publisher API

1. In Cloud Console: **APIs & Services → Library**.
2. Search for **Google Play Android Developer API** and click **Enable**.

### 3. Create a service account and download its JSON key

1. In Cloud Console: **IAM & Admin → Service Accounts → Create service account**.
2. Name it (e.g. `play-verifier`), click **Create and Continue**.
3. Skip the optional steps, click **Done**.
4. Click the service account → **Keys → Add Key → Create new key → JSON**. Download the file.

### 4. Grant the service account access in Play Console

1. Go to <https://play.google.com/console>.
2. **Users and permissions → Invite new users**.
3. Enter the service-account email (`...@<project>.iam.gserviceaccount.com`).
4. Under **App permissions**, grant access to your app(s) and enable at minimum:
   - **View financial data, orders, and cancellation survey responses**
   - **View app information and download bulk reports**
5. Send the invite — it self-accepts.

> Permissions can take a few minutes to propagate. If your first call returns `403`, wait and retry.

### 5. Base64-encode the service account JSON

```bash
# macOS / Linux
base64 -w0 service-account.json
```

```powershell
# Windows PowerShell
[Convert]::ToBase64String([IO.File]::ReadAllBytes("service-account.json"))
```

The output goes into `CredentialsBase64`. Store it as a secret.

### 6. Find your package name

It's on every page of your app in Play Console — e.g. `com.example.app`. Goes into `PackageName`.

### 7. Configure Real-time Developer Notifications (Pub/Sub)

1. In Cloud Console: **Pub/Sub → Topics → Create topic**, e.g. `play-rtdn`. Leave default schema.
2. **Subscriptions → Create subscription**:
   - **Delivery type:** Push.
   - **Endpoint URL:** your public webhook URL (e.g. `https://api.example.com/webhooks/google`). It must be HTTPS.
   - **Enable authentication:** **YES**. Pick the same service account you created above (or any service account — that email becomes the OIDC token's `email` claim, which StoreBridge **requires** to match `ExpectedServiceAccountEmail`).
3. In Cloud Console: **Pub/Sub → Topics → your topic → Permissions**, grant `Pub/Sub Publisher` to `google-play-developer-notifications@system.gserviceaccount.com` (Google's publisher).
4. In Play Console → your app → **Monetize → Monetization setup → Real-time developer notifications**, paste the **full topic name** (`projects/<project-id>/topics/play-rtdn`) and click **Send test notification** to verify wiring — StoreBridge surfaces it as `NotificationEventType.Test`.

### 8. Done — wire it up

```csharp
builder.Services.AddAndroidSubscriptions(opts =>
{
    opts.CredentialsBase64 = configuration["Google:CredentialsBase64"]!;
    opts.PackageName       = configuration["Google:PackageName"]!;
});
builder.Services.AddAndroidInAppPurchases(opts => { /* same two fields */ });
builder.Services.AddAndroidWebhooks(opts =>
{
    opts.WebhookUrl                  = "https://api.example.com/webhooks/google";
    opts.ExpectedServiceAccountEmail = "play-verifier@my-project.iam.gserviceaccount.com"; // required
    opts.ExpectedPackageName         = "com.example.app"; // optional — rejects notifications for other apps
});
// …or bind from configuration: builder.Services.AddAndroidWebhooks(configuration.GetSection("Google:Webhook"));
```

Android options also accept `MaxRetries` (default 3) and `RequestTimeout` (per-attempt HTTP timeout, default 30 s). The Google SDK's own retry policy is disabled so that StoreBridge's retries don't multiply with it.

> **Sandbox for Google:** there is no separate endpoint. Make purchases as a [licensed tester](https://support.google.com/googleplay/android-developer/answer/9845334); their transactions come back with `IsSandbox = true` (the Play API marks them as `testPurchase`).

> **Heads-up: prices.** For auto-renewing Android subscriptions, `PriceAmount` / `PriceDecimal` / `CurrencyCode` come from the line item's `autoRenewingPlan.recurringPrice` — the plan's list price, **excluding taxes and introductory/offer discounts**. Prepaid plans carry no recurring price and come back empty. Google's one-time products endpoint returns no price either; query the Play catalog API if you need it.

---

## Webhook authentication (security boundary)

**Parsing is not authentication.** Any attacker who can guess your webhook URL can POST whatever they like to it. Always run `IWebhookAuthenticator.ValidateAsync` **before** `IWebhookParser.ParseAsync`. If validation throws `WebhookAuthenticationException`, return `401` and stop.

### What gets validated

**Apple — `AppleWebhookAuthenticator`:**
- Reads the `x5c` certificate chain from the signed JWT header.
- Requires `alg = ES256` and exactly three certificates (leaf, intermediate, root), like Apple's own App Store Server Libraries. The leaf key must be P-256 and the signature exactly 64 bytes; oversized headers or certificate entries are rejected before decoding.
- Requires Apple's marker extensions: `1.2.840.113635.100.6.11.1` on the leaf and `1.2.840.113635.100.6.2.1` on the intermediate. Apple Root CA - G3 also anchors other Apple PKI hierarchies; without these checks any EC certificate issued under it would be accepted.
- Builds the X.509 chain leaf → x5c intermediate → **Apple Root CA - G3** (bundled into the assembly), checking every certificate's validity window. Certificate downloads (AIA) are disabled, so attacker-supplied certificates can't make your server fetch URLs.
- Verifies the ES256 signature of the outer JWT against the leaf's public key.
- **Verifies the nested `signedTransactionInfo` and `signedRenewalInfo` JWTs the same way.**
- **Binds the notification to your app:** the `bundleId` (from `data`, `summary` or `externalPurchaseToken`, and in the nested transaction) must equal `BundleId`; production notifications must carry `AppAppleId` when you set it; non-production environments are rejected when `AllowSandbox = false`. A genuine Apple signature alone only proves *Apple* signed it — not that it is for *your* app.
- Bearer token parameter is ignored (Apple's signature is self-contained inside the JWT).
- **Opt-in:** `ValidateCertificateAtSignedDate` evaluates certificate validity at each JWS's `signedDate` instead of now (Apple's libraries do the same when online checks are off), and `EnableOnlineRevocationCheck` adds an OCSP/CRL check. The revocation check only runs after the chain has been anchored to the pinned root and the marker OIDs verified, so attacker-supplied certificates can never trigger outbound requests.

Malformed input never escapes as an unexpected exception: the authenticator throws only `WebhookAuthenticationException`, `FormatException` (body is not a `signedPayload` wrapper / JWT) or `ArgumentException` (empty body). This contract is fuzz-tested, together with end-to-end tests against a notification genuinely signed by Apple.

The bundled Apple Root CA - G3 has these fingerprints — verify against Apple's [Certificate Authority](https://www.apple.com/certificateauthority/) page before deploying to production:

- SHA-256: `63343ABFB89A6A03EBB57E9B3F5FA7BE7C4F5C756F3017B3A8C488C3653E9179`
- Valid 2014-04-30 → 2039-04-30

If Apple rotates roots, pass one or more DER-encoded roots yourself. Download each from the [Apple Certificate Authority](https://www.apple.com/certificateauthority/) page (the `.cer` files are already DER) and pass both during the rotation window:

```csharp
// Trust two roots during a rotation window
var oldRootDer = File.ReadAllBytes("AppleRootCA-G3.cer");
var newRootDer = File.ReadAllBytes("AppleRootCA-G4.cer"); // hypothetical successor
var authenticator = new AppleWebhookAuthenticator(
    new AppleWebhookAuthenticatorOptions { BundleId = "com.example.app" },
    [oldRootDer, newRootDer]);
```

Passing any root via the constructor replaces the bundled one — supply every root you want to trust.

**Google — `AndroidWebhookAuthenticator`:**
- Reads the OIDC bearer token from the `Authorization` header (Pub/Sub attaches one to every push).
- Calls `GoogleJsonWebSignature.ValidateAsync` (signature, issuer = `accounts.google.com`, audience = your webhook URL, expiry).
- **Requires** the `email` claim to equal `ExpectedServiceAccountEmail` and `email_verified = true`. This is mandatory: anyone with a Google Cloud account can mint a Google-signed ID token for an arbitrary audience, so the audience check alone does not prove the request came from *your* Pub/Sub subscription.
- Optionally checks the notification's `packageName` against `ExpectedPackageName`.

### Wiring it up

```csharp
// POST /webhooks/apple
app.MapPost("/webhooks/apple", async (
    HttpRequest http,
    AppleWebhookAuthenticator authenticator,
    AppleWebhookParser parser) =>
{
    var body = await new StreamReader(http.Body).ReadToEndAsync();

    try
    {
        await authenticator.ValidateAsync(body);
    }
    catch (WebhookAuthenticationException)
    {
        return Results.Unauthorized();
    }

    var notification = await parser.ParseAsync(body);
    // notification.NotificationId == Apple's notificationUUID — use it to dedupe retries
    return Results.Ok();
});

// POST /webhooks/google
app.MapPost("/webhooks/google", async (
    HttpRequest http,
    AndroidWebhookAuthenticator authenticator,
    AndroidWebhookParser parser) =>
{
    var body = await new StreamReader(http.Body).ReadToEndAsync();

    try
    {
        await authenticator.ValidateAsync(body, http.Headers.Authorization);
    }
    catch (WebhookAuthenticationException)
    {
        return Results.Unauthorized();
    }

    var notification = await parser.ParseAsync(body);
    // notification.NotificationId == Pub/Sub messageId — use it to dedupe retries
    return Results.Ok();
});
```

> **`NotificationId` is for idempotency.** Both stores retry deliveries; store the ID, process once, ignore duplicates.

---

## Verifying purchases

### Apple — subscription

```csharp
var result = await verifier.VerifySubscriptionAsync(
    originalTransactionId,
    productId: "premium_monthly");   // optional — when given, the transaction MUST be for this product

if (result.IsVerified && result.Status.GrantsAccess())   // Active, Cancelled (still paid) or InGracePeriod
{
    Console.WriteLine($"Expires:           {result.ExpiresAt}");
    Console.WriteLine($"Price:             {result.PriceDecimal} {result.CurrencyCode}");
    Console.WriteLine($"Cancelled by user: {result.CancelledByUser}");
    Console.WriteLine($"Sandbox:           {result.IsSandbox}");
    Console.WriteLine($"In grace until:    {result.GracePeriodExpiresAt}");
    Console.WriteLine($"Next renewal SKU:  {result.AutoRenewProductId}");
    Console.WriteLine($"App account token: {result.ExternalAccountId}");  // appAccountToken, if your app set one
}
```

The API answers with every subscription group of the customer. Apple's `lastTransactions` items carry no product ID, so StoreBridge decodes each `signedTransactionInfo` and matches `productId` against the decoded transaction (no match → failure). Without `productId`, it prefers the item for the transaction ID you passed. The decoded transaction's `bundleId` must also equal the configured `BundleId`.

Use `ExternalAccountId` (Apple `appAccountToken` / Google `obfuscatedExternalAccountId`) to check that the purchase belongs to the user making the request — it stops one account from replaying another account's transaction ID or purchase token.

### Apple — one-time IAP

```csharp
var result = await verifier.VerifyPurchaseAsync(
    transactionId,
    productId: "coins_100");

if (result.IsVerified && result.Status == PurchaseStatus.Purchased)
{
    // result.Status == PurchaseStatus.Refunded if Apple has revoked it
}
```

### Google — subscription

```csharp
// Options-bound shorthand (uses opts.PackageName)
var result = await verifier.VerifySubscriptionAsync(
    purchaseToken,
    productId: "premium_monthly");  // REQUIRED for Google

// Multi-app overload (override the package name explicitly)
var result = await verifier.VerifyAsync(
    "com.example.app", "premium_monthly", purchaseToken);
```

Besides the common fields, Google subscriptions report `IsAcknowledged` (unacknowledged subscriptions are refunded by Google after 3 days), `LinkedPurchaseToken` (the previous token after an upgrade, downgrade or resubscribe — revoke the old entitlement so access isn't granted twice) and `ExternalAccountId`.

Google's `purchases.subscriptionsv2.get` returns *all* line items for the order; StoreBridge uses the one whose `productId` matches what you passed. If none matches, the result is a **failure** — it never falls back to another product, so a token for a cheap subscription can't unlock an expensive one (the Apple verifier behaves the same way when you pass `productId`). If Google returns `410 Gone` (subscription expired ≥60 days ago and was purged), StoreBridge returns `IsVerified = true` with `Status = Expired` — that's the correct outcome, not an error.

### Google — one-time IAP

```csharp
var result = await verifier.VerifyPurchaseAsync(
    purchaseToken,
    productId: "coins_100");   // optional, recommended — when given, the purchase MUST be for this product

if (result.IsVerified && result.Status == PurchaseStatus.Purchased)
{
    // result.Status: Purchased / Consumed / Pending / Cancelled / Refunded
    Console.WriteLine($"Acknowledged:       {result.IsAcknowledged}");
    Console.WriteLine($"Order:              {result.OrderId}");          // matches voided-purchase notifications
    Console.WriteLine($"Units not refunded: {result.RefundableQuantity}"); // multi-quantity partial refunds
}
```

Uses Google's `purchases.productsv2.getproductpurchasev2` (the endpoint Google recommends since one-time products gained multiple purchase options and offers). It only needs the purchase token; the product ID you pass is checked against the purchase's line items. `Refunded` is reported when every unit has been refunded (`refundableQuantity = 0`); with a partial refund the status stays `Purchased` and `RefundableQuantity` tells you how many units are still valid. Acknowledging/consuming purchases is not wrapped by StoreBridge (Google keeps those on `purchases.products`).

### Retries

Both stores' verifiers retry on transient errors (5xx, 429 Too Many Requests, network failures and HTTP timeouts) using fibonacci-spaced delays (1, 2, 3, 5, 8 seconds) with ±25 % jitter. When Apple sends a `Retry-After` header, it is honoured (capped at 30 s). Other 4xx responses are not retried — they aren't transient. Default `MaxRetries = 3` and `RequestTimeout = 30 s`; tune via options.

---

## Handling webhooks

Both stores deliver lifecycle events server-to-server. After authenticating and parsing, you typically switch on the normalized `EventType` and update your local entitlement record. Notifications are *signals*: for anything that grants or removes access, re-read the authoritative state with the verifier (Google explicitly requires this — recent RTDNs no longer even carry the subscription's product ID).

```csharp
switch (notification.EventType)
{
    case NotificationEventType.Renewed:
        // Subscription renewed — extend access through notification.ExpiresAt
        break;

    case NotificationEventType.AutoRenewDisabled:
        // User cancelled — keep access until ExpiresAt, then revoke
        break;

    case NotificationEventType.AutoRenewEnabled:
        // User changed their mind — no action needed
        break;

    case NotificationEventType.GracePeriod:
        // Billing failed — Apple/Google is retrying. Keep limited access.
        break;

    case NotificationEventType.InBillingRetry:
        // Billing failed with no (or after the) grace period — store keeps retrying. Suspend access.
        break;

    case NotificationEventType.Refunded:
        // Revoked by the store — remove access immediately.
        break;

    case NotificationEventType.RefundReversed:
        // Apple reversed a refund after a dispute — reinstate access.
        break;

    case NotificationEventType.Expired:
        // Billing period ended — revoke access.
        break;

    case NotificationEventType.Test:
        // Wiring smoke-test from App Store Connect / Play Console — no-op.
        break;

    case NotificationEventType.Other:
        // Event we don't normalize yet. Inspect notification.RawEventType for the platform value.
        break;
}
```

For one-time purchase webhooks, use `IInAppPurchaseWebhookParser` and switch on `InAppPurchaseEventType`:

```csharp
switch (purchaseNotification.EventType)
{
    case InAppPurchaseEventType.Purchased:      // Apple ONE_TIME_CHARGE, Google ONE_TIME_PRODUCT_PURCHASED
    case InAppPurchaseEventType.Refunded:       // Apple REFUND/REVOKE, Google voidedPurchaseNotification
    case InAppPurchaseEventType.RefundReversed: // Apple REFUND_REVERSED — reinstate the content
    case InAppPurchaseEventType.Cancelled:      // Google one-time product cancelled
    case InAppPurchaseEventType.ConsumptionRequest:  // Apple consumption request for a consumable
    case InAppPurchaseEventType.RefundReviewRequested: // Google chargeback review — answer within 24 h
    case InAppPurchaseEventType.Test:           // wiring smoke test
    case InAppPurchaseEventType.Other:          // see purchaseNotification.RawEventType
}
```

> **About `voidedPurchaseNotification`:** Google's voided-purchase payload doesn't carry the SKU, only an `orderId`, which StoreBridge surfaces in `OrderId` (`ProductId` stays empty). Correlate via `PurchaseId` (the purchase token) or `OrderId`. Voided **subscriptions** (`productType = 1`) are handled **only** by `AndroidWebhookParser` (as `Refunded` / `Revoked`); `AndroidInAppPurchaseWebhookParser` throws `FormatException` for them, so try the subscription parser first (as the sample app does).

> **About `pendingRefundReviewNotification`:** Google now asks developers to review some chargebacks. StoreBridge maps it to `RefundReviewRequested` with `OrderId` and `RefundReviewToken`; send your suggestion to the Play Developer API `orders.reviewRefund` (not wrapped by StoreBridge) within 24 hours.

> **Unrecognized notifications:** both stores keep adding notification types. Unknown Apple types map to `Other`; a Google payload neither parser understands throws `FormatException` — acknowledge it with a `2xx` anyway, or Pub/Sub will redeliver it indefinitely.

---

## Normalized models reference

```csharp
// SubscriptionVerificationResult
result.IsVerified           // bool — API call succeeded and response is valid
result.Status               // SubscriptionStatus enum
result.Store                // Store.Apple or Store.Android
result.SubscriptionId       // originalTransactionId (Apple) / purchaseToken (Google)
result.ProductId            // product / subscription identifier
result.ExpiresAt            // DateTimeOffset?
result.PurchasedAt          // DateTimeOffset?
result.CancelledByUser      // bool — Apple: auto-renew off; Google: user-initiated cancellation
result.IsPromotional        // bool — trial, intro, or offer
result.IsSandbox            // bool — sandbox / test purchase
result.PriceAmount          // long — thousandths (Apple) / micros (Google; recurring list price)
result.PriceDecimal         // decimal — human-readable
result.CurrencyCode         // ISO 4217 (e.g. "USD")
result.AutoRenewProductId   // Apple only — SKU of the next renewal cycle (may differ on plan change)
result.GracePeriodExpiresAt // Apple only
result.IsAcknowledged       // Google only — false for Apple
result.LinkedPurchaseToken  // Google only — previous token after upgrade/downgrade/resubscribe
result.ExternalAccountId    // Apple appAccountToken / Google obfuscatedExternalAccountId
result.ErrorMessage         // string? — set when IsVerified = false

// InAppPurchaseVerificationResult
result.IsVerified
result.Status               // PurchaseStatus enum
result.Store
result.PurchaseId           // transactionId (Apple) / purchaseToken (Google)
result.ProductId
result.PurchasedAt
result.Quantity             // defaults to 1
result.RefundableQuantity   // int? — Google only: units not refunded yet
result.OrderId              // Google order ID (e.g. "GPA.…"); empty for Apple
result.IsAcknowledged       // Google only
result.IsSandbox
result.PriceAmount
result.PriceDecimal
result.CurrencyCode
result.ErrorMessage

// SubscriptionNotification (webhooks)
notification.EventType        // NotificationEventType
notification.RawEventType     // platform-specific raw type string
notification.NotificationId   // Apple notificationUUID / Google messageId — DEDUPE WITH THIS
notification.Store
notification.SubscriptionId
notification.ProductId
notification.Status           // SubscriptionStatus at the time of the event
notification.ExpiresAt
notification.EventAt
notification.IsSandbox

// InAppPurchaseNotification (webhooks)
notification.EventType        // InAppPurchaseEventType
notification.RawEventType
notification.NotificationId
notification.Store
notification.PurchaseId
notification.ProductId
notification.OrderId          // Google voided / refund-review notifications
notification.RefundReviewToken // Google refund-review notifications
notification.EventAt
notification.IsSandbox        // always false for Google RTDNs (no test flag) — use the verifier
```

### `SubscriptionStatus`

| Value | Meaning |
|---|---|
| `Active` | Subscription is active and paid |
| `Expired` | Billing period ended, no access |
| `Cancelled` | Auto-renew off but still within the paid period — keep access until `ExpiresAt` |
| `InGracePeriod` | Payment failed, store retrying, access kept |
| `InBillingRetry` | Payment failed, no (or after the) grace period, store retrying — no access |
| `Revoked` | Subscription revoked by the store |
| `Unknown` | Unrecognized state from the store (also Google `PAUSED` / `PENDING`) |

Use `status.GrantsAccess()` to decide entitlement: it is `true` for `Active`, `Cancelled` and `InGracePeriod`. Both stores and both paths (verifier and webhook) use the same meaning — e.g. an Apple subscription with auto-renew off and a Google `SUBSCRIPTION_STATE_CANCELED` are both `Cancelled`.

### `PurchaseStatus`

| Value | Meaning |
|---|---|
| `Purchased` | Successful, not yet consumed |
| `Consumed` | Consumed (Google only) |
| `Cancelled` | Cancelled before completion |
| `Pending` | Awaiting parental approval or bank confirmation |
| `Refunded` | Refunded / revoked |
| `Unknown` | Could not be determined |

### `NotificationEventType`

| Value | Platforms |
|---|---|
| `Renewed` | Both |
| `AutoRenewDisabled` | Both — user turned off auto-renew |
| `AutoRenewEnabled` | Both — user re-enabled auto-renew (Apple `DID_CHANGE_RENEWAL_STATUS:AUTO_RENEW_ENABLED`, Google `SUBSCRIPTION_RESTARTED`). A `DID_CHANGE_RENEWAL_STATUS` without a known subtype maps to `Other` |
| `Created` | Both |
| `InBillingRetry` | Both — billing retry without/after grace period (Apple `DID_FAIL_TO_RENEW` w/o subtype, `GRACE_PERIOD_EXPIRED`; Google `ON_HOLD`) |
| `GracePeriod` | Both — billing failed, retrying |
| `Refunded` | Both |
| `Cancelled` | Both |
| `Expired` | Both |
| `Test` | Both — test notification from App Store Connect / Play Console |
| `Other` | Fallback for unmapped notification types |
| `RefundReversed` | Apple — `REFUND_REVERSED`; reinstate access |

### `InAppPurchaseEventType`

| Value | Platforms |
|---|---|
| `Purchased` | Both — Apple `ONE_TIME_CHARGE`, Google `ONE_TIME_PRODUCT_PURCHASED` |
| `Refunded` | Both — Apple `REFUND`/`REVOKE`, Google `voidedPurchaseNotification` |
| `Cancelled` | Google — one-time product cancelled |
| `ConsumptionRequest` | Apple — consumption request for a consumable |
| `Test` | Both — test notification |
| `Other` | Fallback |
| `RefundReversed` | Apple — `REFUND_REVERSED`; reinstate the content |
| `RefundReviewRequested` | Google — `pendingRefundReviewNotification`; respond via `orders.reviewRefund` within 24 h |

### `PriceConverter`

Helper for raw amounts when you need them outside the verifiers:

```csharp
decimal usd = PriceConverter.FromApplePrice(1990);       // 1.99
decimal eur = PriceConverter.FromGoogleMicros(1_990_000); // 1.99
```

---

## Robust exception handling

StoreBridge follows a deliberate two-tier exception policy. You can rely on it; the unit tests pin every path below.

### Tier 1 — Configuration errors fail fast

Each DI extension automatically registers an `IValidateOptions<T>` that runs the **first time the options are read** (when the verifier or authenticator is resolved). A misconfigured app fails its DI smoke test in CI, not in production at 3 a.m. on the first webhook.

What gets validated at startup:

| Option | Check |
|---|---|
| `AppleApiOptions.KeyId` / `IssuerId` / `BundleId` | Required, non-blank |
| `AppleApiOptions.PrivateKeyBase64` | Required, valid base64, imports as PKCS#8 ECDSA — catches `.p8` files that still have the `-----BEGIN/END-----` headers, are uploaded as a binary blob, or are simply the wrong key |
| `AppleApiOptions.MaxRetries` | `>= 1` |
| `AppleSubscriptionOptions.SubscriptionsBaseUrl` / `AppleInAppPurchaseOptions.TransactionsBaseUrl` | Absolute http(s) URI |
| `SandboxSubscriptionsBaseUrl` / `SandboxTransactionsBaseUrl` | Absolute http(s) URI when `SandboxFallback` is on |
| `AppleWebhookAuthenticatorOptions.BundleId` | Required, non-blank |
| `AndroidOptions.PackageName` | Required, non-blank |
| `AndroidOptions.CredentialsBase64` | Required, valid base64, parses as a **service-account** key through `CredentialFactory.FromStream<ServiceAccountCredential>` — catches wrong file format, truncated downloads, OAuth-client / `authorized_user` / `external_account` JSON pasted instead of a service-account key |
| `AndroidOptions.MaxRetries` | `>= 1` (defaulted to 3 if not set) |
| `AndroidWebhookAuthenticatorOptions.WebhookUrl` | Required, absolute http(s) URI (must match the audience claim of the OIDC token Google sends) |
| `AndroidWebhookAuthenticatorOptions.ExpectedServiceAccountEmail` | Required, looks like an email |

A failure raises `OptionsValidationException` with a remediation message naming the offending field:

```
OptionsValidationException: PrivateKeyBase64 could not be imported as a PKCS#8 ECDSA key.
Strip the '-----BEGIN/END PRIVATE KEY-----' headers and base64-encode the body only.
```

If you construct a verifier manually (without DI), the same validation kicks in lazily on the first verification call — `AppleJwtHelper.GenerateToken` wraps `FormatException` / `CryptographicException` into `InvalidOperationException` with the same clear message, which the verifier surfaces in `result.ErrorMessage`.

### Tier 2 — Transient/external errors become `Failure` results

The verifiers never let store-side or network exceptions escape to your handler. They turn them into a result with `IsVerified = false` and a populated `ErrorMessage`. Caught and handled:

| Exception | Handling |
|---|---|
| `HttpRequestException` (Apple) / `Google.GoogleApiException` (Google) | Retried with fibonacci backoff (1, 2, 3, 5, 8 s) up to `MaxRetries` if transient (5xx, 429, or no status). Then returned as `Failure`. |
| HTTP timeout (`TaskCanceledException` while *your* token is not cancelled) | Retried like a transient error, then → `Failure` with `"... timed out"` |
| `TokenResponseException` (Google OAuth token exchange — e.g. deleted/revoked service-account key) | → `Failure` with `"Google credentials error"` |
| `Google.GoogleApiException` with `HttpStatusCode == 410 Gone` (subscription verifier) | Returned as `IsVerified = true, Status = Expired` — that *is* the correct outcome for a subscription expired 60+ days ago and purged from the Play backend |
| `JsonException` | Body could not be parsed → `Failure` with `"Apple/Google API returned a malformed response"` |
| `InvalidOperationException` / `FormatException` from JWT/credential setup | → `Failure` with the original remediation message in `ErrorMessage` (credential loading is retried on the next call, not cached as a failure) |
| Empty `originalTransactionId` / `purchaseToken` / `packageName` / `productId` | → `Failure` without hitting the network |
| `OperationCanceledException` / `TaskCanceledException` caused by **your** `cancellationToken` | **Always propagates.** Each verifier calls `cancellationToken.ThrowIfCancellationRequested()` on entry so cancellation works even with fake HTTP handlers in tests |
| Signature/bundle mismatch in the API response (`VerifyResponseSignatures`, configured `BundleId`) | → `Failure` |

All four verifiers and `AppleWebhookAuthenticator` implement `IDisposable` (the DI container disposes them); calling one after `Dispose` throws `ObjectDisposedException`.

Webhook auth/parse exceptions are intentionally NOT caught by the library — you want to return `401` on `WebhookAuthenticationException` and `400` on `FormatException` / `ArgumentException` from the parsers. Those are the only exception types they throw for bad input (fuzz-tested). Wrap them yourself (see [Wiring it up](#wiring-it-up) above).

### Retries — what is transient

Both `AppleRetryHelper` and `AndroidRetryHelper` use the same backoff schedule (1, 2, 3, 5, 8 seconds). Transient means:

- HTTP 5xx (server error)
- HTTP 429 (Too Many Requests — both stores rate-limit)
- `HttpRequestException` with no status code (network/DNS failure)
- `Google.GoogleApiException` with `HttpStatusCode == 0`
- HTTP timeouts (`HttpClient.Timeout` elapsed)

4xx other than 429 propagates immediately — it's a permanent caller error (bad key, missing scope, wrong package) and retrying would just burn quota.

### What you should still do in your handler

```csharp
app.MapPost("/verify/apple", async (AppleSubscriptionVerifier v, VerifyRequest req, ILogger<Program> log) =>
{
    var result = await v.VerifySubscriptionAsync(req.OriginalTransactionId, req.ProductId);

    if (!result.IsVerified)
    {
        log.LogWarning("Apple verify failed: {Error}", result.ErrorMessage);
        return Results.UnprocessableEntity(new { error = result.ErrorMessage });
    }

    return Results.Ok(result);
});
```

You don't need a try/catch around the verifier call — the only thing that can throw is `OperationCanceledException` (your caller cancelled) or `OptionsValidationException` (DI-time config error, which you should hit before deployment).

### Logging

The verifiers never log full purchase tokens or transaction IDs: they appear masked (`abcd…wxyz (len 150)`), enough to correlate log lines without leaking the identifier.

---

## Sample web app

`samples/StoreBridge.Sample.Web/` is a minimal ASP.NET Core host that wires up both DI extension sets and exposes a webhook + verification endpoint per platform. It doubles as a **real-payload validation harness** — point Apple's `requestTestNotification` and Google's Play Console **Send test notification** at it and inspect exactly what the library parsed.

```bash
cd samples/StoreBridge.Sample.Web

# Configure secrets (never put real keys in appsettings.json)
dotnet user-secrets init
dotnet user-secrets set "Apple:KeyId"              "ABC1234567"
dotnet user-secrets set "Apple:IssuerId"           "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx"
dotnet user-secrets set "Apple:BundleId"           "com.example.app"
dotnet user-secrets set "Apple:PrivateKeyBase64"   "<base64 of the .p8 key body>"

dotnet user-secrets set "Google:CredentialsBase64" "<base64 of service-account.json>"
dotnet user-secrets set "Google:PackageName"       "com.example.app"
dotnet user-secrets set "Google:Webhook:WebhookUrl" "https://<your-tunnel>/webhooks/google"

dotnet run
# GET http://localhost:5000/   — lists the available endpoints
```

The Apple config defaults to the **sandbox** endpoints — change them in `appsettings.json` if you want to verify production transactions.

Smoke-test a verification call with a sandbox transaction:

```bash
curl -X POST http://localhost:5000/verify/apple/subscription \
  -H "Content-Type: application/json" \
  -d '{ "token": "<originalTransactionId>", "productId": "premium_monthly" }'

curl -X POST http://localhost:5000/verify/google/subscription \
  -H "Content-Type: application/json" \
  -d '{ "token": "<purchaseToken>", "productId": "premium_monthly" }'
```

See [`samples/StoreBridge.Sample.Web/README.md`](https://github.com/jjalcantara-dev/StoreBridge/blob/main/samples/StoreBridge.Sample.Web/README.md) for tunneling instructions and step-by-step webhook validation.

---

## Changelog and contributing

- What changed in each version: [CHANGELOG.md](https://github.com/jjalcantara-dev/StoreBridge/blob/main/CHANGELOG.md).
- Building from source, running the tests and releasing: [CONTRIBUTING.md](https://github.com/jjalcantara-dev/StoreBridge/blob/main/CONTRIBUTING.md).

All tests are unit tests (no calls to live Apple/Google APIs). They include a notification genuinely signed by Apple, verified against the real Apple Root CA - G3 chain, and seeded fuzz tests of every webhook entry point.

---

## Trademarks

Apple, App Store, iOS, and StoreKit are trademarks of Apple Inc., registered in the U.S. and other countries. Google, Google Play, and Android are trademarks of Google LLC. This library is an independent open-source project and is **not affiliated with, endorsed by, or sponsored by** Apple Inc. or Google LLC. All other product names, logos, and brands are property of their respective owners and are used in this README for descriptive purposes only.

---

## License

[MIT](https://github.com/jjalcantara-dev/StoreBridge/blob/main/LICENSE). Use it however you want.
