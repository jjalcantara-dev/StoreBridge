# StoreBridge

**One .NET library for verifying and handling Apple App Store and Google Play subscriptions and in-app purchases.** StoreBridge normalizes the two stores' very different APIs and webhook formats behind one set of interfaces, so you can write your billing logic once and run it against both. Targets `net8.0`, `net9.0`, and `net10.0`. Drops cleanly into ASP.NET Core APIs, Azure / AWS Functions, background workers, or plain console apps.

> **Status:** all 197 unit tests pass on every target framework. The library does the cryptographic heavy lifting (Apple x5c chain validation against Apple Root CA - G3, Google OIDC token validation for Pub/Sub) so you don't have to roll your own.

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
13. [Building from source](#building-from-source)
14. [Releasing new versions](#releasing-new-versions)
15. [Trademarks](#trademarks)
16. [License](#license)

---

## What you get

| Capability | StoreBridge | Hand-rolling it |
|---|---|---|
| Verify Apple subscription / one-time IAP via App Store Server API v1 | ✅ | Generate ES256 JWT, sign with `.p8` key, call API, parse nested signed JWTs |
| Verify Google Play subscription / one-time IAP via Android Publisher v3 | ✅ | Wire up service account, build OAuth client, call subscriptionsv2 / products endpoints |
| Cryptographically authenticate Apple Server Notifications v2 | ✅ | Walk x5c chain, anchor to Apple Root CA - G3, verify ES256 over JWT and nested JWTs |
| Cryptographically authenticate Google Pub/Sub push webhooks | ✅ | Validate OIDC bearer token (signature, audience, issuer, expiry, service-account email) |
| Normalized status / event enums across both stores | ✅ | Maintain two separate switch statements forever |
| Retry on transient failures with sensible defaults | ✅ (1/2/3/5/8s, both stores) | Roll your own Polly policies |
| **Fail-fast on misconfiguration** | ✅ `IValidateOptions<T>` validates `.p8` import and service-account JSON at startup | Surfaces as cryptic errors during the first real webhook |
| **Robust runtime exception handling** | ✅ HTTP, JSON, credential, cancellation paths all covered | Easy to leak a `JsonException` or `CryptographicException` out to the response |
| Deduplicate webhook retries | ✅ via `NotificationId` | Track Apple `notificationUUID` + Google `messageId` yourself |
| Sandbox vs production | ✅ swap one URL | Track two endpoints per platform |

If you only need one store today, install only that package — there is no transitive dependency on the other.

---

## Packages

| Package | What's in it | NuGet |
|---|---|---|
| `StoreBridge` | Core abstractions and shared models (`ISubscriptionVerifier`, `IInAppPurchaseVerifier`, `IWebhookParser`, `IWebhookAuthenticator`, all DTOs and enums). No platform deps. | `StoreBridge` |
| `StoreBridge.Apple` | App Store Server API v1 + Server Notifications v2. Pulls in `StoreBridge`, `Microsoft.IdentityModel.JsonWebTokens`, `System.IdentityModel.Tokens.Jwt`. | `StoreBridge.Apple` |
| `StoreBridge.Android` | Google Play Developer API v3 + Pub/Sub. Pulls in `StoreBridge` and `Google.Apis.AndroidPublisher.v3`. | `StoreBridge.Android` |

All packages ship symbols (`.snupkg`) and Source Link metadata so consumers can step into the source from a debugger.

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
builder.Services.AddAppleWebhooks();

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

    // Optional: retry budget for 5xx / network failures (default 3)
    // opts.MaxRetries = 3;
});
builder.Services.AddAppleInAppPurchases(opts => { /* same four fields */ });
builder.Services.AddAppleWebhooks();
```

### Sandbox vs Production for Apple

Just swap the base URL — everything else is identical:

```csharp
// Sandbox
opts.SubscriptionsBaseUrl = "https://api.storekit-sandbox.itunes.apple.com/inApps/v1/subscriptions/";
opts.TransactionsBaseUrl  = "https://api.storekit-sandbox.itunes.apple.com/inApps/v1/transactions/";

// Production (the defaults)
opts.SubscriptionsBaseUrl = "https://api.storekit.itunes.apple.com/inApps/v1/subscriptions/";
opts.TransactionsBaseUrl  = "https://api.storekit.itunes.apple.com/inApps/v1/transactions/";
```

`SubscriptionVerificationResult.IsSandbox` tells you which environment the *transaction* actually came from, regardless of which endpoint you hit.

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
   - **Enable authentication:** **YES**. Pick the same service account you created above (or any service account — that email becomes the OIDC token's `email` claim, which StoreBridge can verify against `ExpectedServiceAccountEmail`).
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
    opts.ExpectedServiceAccountEmail = "play-verifier@my-project.iam.gserviceaccount.com";
});
```

> **Sandbox for Google:** there is no separate endpoint. Make purchases as a [licensed tester](https://support.google.com/googleplay/android-developer/answer/9845334); their transactions come back with `IsSandbox = true` (the Play API marks them as `testPurchase`).

> **Heads-up: subscription prices.** Google's `purchases.subscriptionsv2.get` endpoint does **not** return the price in the purchase response. `SubscriptionVerificationResult.PriceAmount` / `PriceDecimal` / `CurrencyCode` are returned empty for Android subscriptions — query the Play catalog API separately if you need the price. One-time purchase prices are also not returned by the products endpoint and follow the same rule.

---

## Webhook authentication (security boundary)

**Parsing is not authentication.** Any attacker who can guess your webhook URL can POST whatever they like to it. Always run `IWebhookAuthenticator.ValidateAsync` **before** `IWebhookParser.ParseAsync`. If validation throws `WebhookAuthenticationException`, return `401` and stop.

### What gets validated

**Apple — `AppleWebhookAuthenticator`:**
- Reads the `x5c` certificate chain from the signed JWT header.
- Builds the X.509 chain with the leaf as the entity, intermediates as extras, and **anchors to Apple Root CA - G3** (PEM bundled into the assembly).
- Verifies the leaf certificate's validity window.
- Verifies the ES256 signature of the outer JWT against the leaf's public key.
- **Recursively verifies the nested `signedTransactionInfo` and `signedRenewalInfo` JWTs the same way.**
- Bearer token parameter is ignored (Apple's signature is self-contained inside the JWT).

The bundled Apple Root CA - G3 has these fingerprints — verify against Apple's [Certificate Authority](https://www.apple.com/certificateauthority/) page before deploying to production:

- SHA-256: `63343ABFB89A6A03EBB57E9B3F5FA7BE7C4F5C756F3017B3A8C488C3653E9179`
- Valid 2014-04-30 → 2039-04-30

If Apple rotates roots, pass one or more DER-encoded roots yourself. Download each from the [Apple Certificate Authority](https://www.apple.com/certificateauthority/) page (the `.cer` files are already DER) and pass both during the rotation window:

```csharp
// Trust two roots during a rotation window
var oldRootDer = File.ReadAllBytes("AppleRootCA-G3.cer");
var newRootDer = File.ReadAllBytes("AppleRootCA-G4.cer"); // hypothetical successor
var authenticator = new AppleWebhookAuthenticator(
    new[] { oldRootDer, newRootDer });
```

Passing any root via the constructor replaces the bundled one — supply every root you want to trust.

**Google — `AndroidWebhookAuthenticator`:**
- Reads the OIDC bearer token from the `Authorization` header (Pub/Sub attaches one to every push).
- Calls `GoogleJsonWebSignature.ValidateAsync` (signature, issuer = `accounts.google.com`, audience = your webhook URL, expiry).
- Optionally verifies the `email` claim equals `ExpectedServiceAccountEmail`.

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
    productId: "premium_monthly");   // optional — disambiguates when a customer has multiple

if (result.IsVerified && result.Status == SubscriptionStatus.Active)
{
    Console.WriteLine($"Expires:           {result.ExpiresAt}");
    Console.WriteLine($"Price:             {result.PriceDecimal} {result.CurrencyCode}");
    Console.WriteLine($"Cancelled by user: {result.CancelledByUser}");
    Console.WriteLine($"Sandbox:           {result.IsSandbox}");
    Console.WriteLine($"In grace until:    {result.GracePeriodExpiresAt}");
    Console.WriteLine($"Next renewal SKU:  {result.AutoRenewProductId}");
}
```

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

Google's `purchases.subscriptionsv2.get` returns *all* line items for the order; StoreBridge picks the one whose `productId` matches what you passed, falling back to the first if none match. If Google returns `410 Gone` (subscription expired ≥60 days ago and was purged), StoreBridge returns `IsVerified = true` with `Status = Expired` — that's the correct outcome, not an error.

### Google — one-time IAP

```csharp
var result = await verifier.VerifyPurchaseAsync(
    purchaseToken,
    productId: "coins_100");

if (result.IsVerified)
{
    // result.Status: Purchased / Consumed / Pending / Cancelled
    Console.WriteLine($"Acknowledged: {result.IsAcknowledged}");
}
```

### Retries

Apple verifiers retry on transient HTTP errors (5xx and `HttpRequestException` with no status code) using fibonacci-spaced delays (1, 2, 3, 5, 8 seconds). 4xx responses propagate immediately — they aren't transient. Default `MaxRetries = 3`; tune via options. The signing JWT is generated once per call and reused across retries.

---

## Handling webhooks

Both stores deliver lifecycle events server-to-server. After authenticating and parsing, you typically switch on the normalized `EventType` and update your local entitlement record:

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
        // Grace period ended — account on hold. Suspend access.
        break;

    case NotificationEventType.Refunded:
        // Revoked by the store — remove access immediately.
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
    case InAppPurchaseEventType.Purchased:      // one-time purchase completed
    case InAppPurchaseEventType.Refunded:       // Apple REFUND/REVOKE, Google voidedPurchaseNotification
    case InAppPurchaseEventType.Cancelled:      // Google one-time product cancelled
    case InAppPurchaseEventType.ConsumptionRequest:  // Apple consumption request for a consumable
    case InAppPurchaseEventType.Test:           // wiring smoke test
    case InAppPurchaseEventType.Other:          // see purchaseNotification.RawEventType
}
```

> **About `voidedPurchaseNotification`:** Google's voided-purchase payload doesn't carry the SKU, only an `orderId`. StoreBridge surfaces that `orderId` in `ProductId` for refunded notifications so you have *something* to correlate against — look up the SKU from your own records or via the Voided Purchases API if you need it.

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
result.CancelledByUser      // bool — auto-renew disabled (still active until ExpiresAt)
result.IsPromotional        // bool — trial, intro, or offer
result.IsSandbox            // bool — sandbox / test purchase
result.PriceAmount          // long — thousandths (Apple) / micros (Google); 0 for Android sub
result.PriceDecimal         // decimal — human-readable; 0 for Android sub
result.CurrencyCode         // ISO 4217 (e.g. "USD"); empty for Android sub
result.AutoRenewProductId   // Apple only — SKU of the next renewal cycle (may differ on plan change)
result.GracePeriodExpiresAt // Apple only
result.ErrorMessage         // string? — set when IsVerified = false

// InAppPurchaseVerificationResult
result.IsVerified
result.Status               // PurchaseStatus enum
result.Store
result.PurchaseId           // transactionId (Apple) / purchaseToken (Google)
result.ProductId
result.PurchasedAt
result.Quantity             // defaults to 1
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
notification.EventAt
notification.IsSandbox
```

### `SubscriptionStatus`

| Value | Meaning |
|---|---|
| `Active` | Subscription is active and paid |
| `Expired` | Billing period ended, no access |
| `Cancelled` | Cancelled but still within the billing period |
| `InGracePeriod` | Payment failed, store retrying, limited access |
| `InBillingRetry` | Grace period ended, account on hold |
| `Revoked` | Subscription revoked by the store |
| `Unknown` | Unrecognized state from the store |

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
| `AutoRenewEnabled` | Both — user re-enabled auto-renew |
| `Created` | Both |
| `InBillingRetry` | Both — account on hold after grace period |
| `GracePeriod` | Both — billing failed, retrying |
| `Refunded` | Both |
| `Cancelled` | Both |
| `Expired` | Both |
| `Test` | Both — test notification from App Store Connect / Play Console |
| `Other` | Fallback for unmapped notification types |

### `InAppPurchaseEventType`

| Value | Platforms |
|---|---|
| `Purchased` | Both |
| `Refunded` | Both — Apple `REFUND`/`REVOKE`, Google `voidedPurchaseNotification` |
| `Cancelled` | Google — one-time product cancelled |
| `ConsumptionRequest` | Apple — consumption request for a consumable |
| `Test` | Both — test notification |
| `Other` | Fallback |

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
| `AndroidOptions.PackageName` | Required, non-blank |
| `AndroidOptions.CredentialsBase64` | Required, valid base64, parses through `GoogleCredential.FromStream` — catches wrong file format, truncated downloads, OAuth-client JSON pasted instead of service-account JSON |
| `AndroidOptions.MaxRetries` | `>= 1` (defaulted to 3 if not set) |
| `AndroidWebhookAuthenticatorOptions.WebhookUrl` | Required, absolute http(s) URI (must match the audience claim of the OIDC token Google sends) |

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
| `Google.GoogleApiException` with `HttpStatusCode == 410 Gone` (subscription verifier) | Returned as `IsVerified = true, Status = Expired` — that *is* the correct outcome for a subscription expired 60+ days ago and purged from the Play backend |
| `JsonException` | Body could not be parsed → `Failure` with `"Apple/Google API returned a malformed response"` |
| `InvalidOperationException` from JWT/credential setup | → `Failure` with the original remediation message in `ErrorMessage` |
| Empty `originalTransactionId` / `purchaseToken` / `packageName` / `productId` | → `Failure` without hitting the network |
| `OperationCanceledException` / `TaskCanceledException` | **Always propagates.** Each verifier calls `cancellationToken.ThrowIfCancellationRequested()` on entry so cancellation works even with fake HTTP handlers in tests |

Webhook auth/parse exceptions are intentionally NOT caught by the library — you want to return `401` on `WebhookAuthenticationException` and `400` on `FormatException` from the parsers. Wrap them yourself (see [Wiring it up](#wiring-it-up) above).

### Retries — what is transient

Both `AppleRetryHelper` and `AndroidRetryHelper` use the same backoff schedule (1, 2, 3, 5, 8 seconds). Transient means:

- HTTP 5xx (server error)
- HTTP 429 (Google only — Apple does not advertise rate limits here)
- `HttpRequestException` with no status code (network/DNS failure)
- `Google.GoogleApiException` with `HttpStatusCode == 0`

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

See [`samples/StoreBridge.Sample.Web/README.md`](samples/StoreBridge.Sample.Web/README.md) for tunneling instructions and step-by-step webhook validation.

---

## Building from source

```bash
git clone https://github.com/jjalcantara-dev/StoreBridge.git
cd StoreBridge

# Restore + build all three target frameworks (net8, net9, net10)
dotnet restore
dotnet build --configuration Release

# Run the full test suite (197 unit tests, all green)
dotnet test --configuration Release

# Just one project
dotnet test tests/StoreBridge.Apple.Tests   --configuration Release
dotnet test tests/StoreBridge.Android.Tests --configuration Release

# Produce NuGet packages locally
dotnet pack --configuration Release /p:Version=1.0.0-local --output ./nupkgs
```

Repo layout:

```
src/
  StoreBridge/             Core abstractions, models, enums
  StoreBridge.Apple/       App Store Server API v1 + Server Notifications v2
  StoreBridge.Android/     Android Publisher v3 + Pub/Sub
tests/
  StoreBridge.Apple.Tests/    xUnit + FakeHttpMessageHandler  (104 tests)
  StoreBridge.Android.Tests/  xUnit + NSubstitute             ( 93 tests)
samples/
  StoreBridge.Sample.Web/  Minimal ASP.NET Core harness
```

All tests are unit tests — there are no integration tests against real Apple/Google APIs. Use the sample web app for end-to-end validation with sandbox credentials.

---

## Releasing new versions

Releases are fully automated via GitHub Actions ([`.github/workflows/publish.yml`](.github/workflows/publish.yml)). Push a semver git tag and the workflow restores, builds, tests, packs all three NuGet packages, and pushes them to NuGet.org using the `NUGET_API_KEY` repository secret:

```bash
git tag v1.2.0
git push origin v1.2.0
```

The workflow extracts the version from the tag (`v1.2.0` → `1.2.0`), so you don't edit `Version` in source.

---

## Trademarks

Apple, App Store, iOS, and StoreKit are trademarks of Apple Inc., registered in the U.S. and other countries. Google, Google Play, and Android are trademarks of Google LLC. This library is an independent open-source project and is **not affiliated with, endorsed by, or sponsored by** Apple Inc. or Google LLC. All other product names, logos, and brands are property of their respective owners and are used in this README for descriptive purposes only.

---

## License

[MIT](LICENSE). Use it however you want.
