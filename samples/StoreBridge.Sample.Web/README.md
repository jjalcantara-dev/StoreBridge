# StoreBridge.Sample.Web

A minimal ASP.NET Core host that wires up the `StoreBridge.Apple` and
`StoreBridge.Android` packages through their DI extensions. It doubles as a
**real-payload validation harness** — point Apple and Google test notifications at it
and inspect exactly what the library parsed.

## Why this exists

The library's 197 unit tests all use synthetic data. Before trusting it in production,
validate it against at least one **real** payload per platform. Apple and Google both let
you send real, signed test notifications without a published app.

## Configure

Never put secrets in `appsettings.json`. Use [user-secrets](https://learn.microsoft.com/aspnet/core/security/app-secrets):

```bash
cd samples/StoreBridge.Sample.Web
dotnet user-secrets init

# Apple — from App Store Connect → Users and Access → Integrations → In-App Purchase
dotnet user-secrets set "Apple:KeyId" "ABC1234567"
dotnet user-secrets set "Apple:IssuerId" "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx"
dotnet user-secrets set "Apple:BundleId" "com.example.app"
dotnet user-secrets set "Apple:PrivateKeyBase64" "<base64 of the .p8 key body>"

# Google — base64 of the service-account JSON
dotnet user-secrets set "Google:CredentialsBase64" "<base64 of service-account.json>"
dotnet user-secrets set "Google:PackageName" "com.example.app"
dotnet user-secrets set "Google:Webhook:WebhookUrl" "https://<your-tunnel>/webhooks/google"
```

The `Apple` section defaults to the **sandbox** App Store endpoints — change them in
`appsettings.json` for production transactions.

## Run

```bash
dotnet run
# GET http://localhost:5000/  → lists the endpoints
```

## Validate webhooks against real payloads

1. Expose the host publicly with a tunnel (`ngrok http 5000`, `dev tunnels`, etc.).
2. **Apple:** set the tunnel URL as the Server Notifications URL in App Store Connect,
   then call the App Store Server API `requestTestNotification` endpoint. Apple sends a
   real, signed `TEST` notification to `POST /webhooks/apple`.
3. **Google:** configure Real-time Developer Notifications (Pub/Sub) with a push
   subscription to the tunnel URL, then press **Send test notification** in the Play
   Console. It arrives at `POST /webhooks/google`.

Both endpoints authenticate the payload (`IWebhookAuthenticator`) before parsing and
return the normalized notification as JSON — a `401` means authentication failed.

## Validate verification against real tokens

Make a sandbox purchase (Apple sandbox tester / Google license tester), grab the
transaction ID or purchase token, and POST it:

```bash
curl -X POST http://localhost:5000/verify/apple/subscription \
  -H "Content-Type: application/json" \
  -d '{ "token": "<originalTransactionId>", "productId": "premium_monthly" }'

curl -X POST http://localhost:5000/verify/google/subscription \
  -H "Content-Type: application/json" \
  -d '{ "token": "<purchaseToken>", "productId": "premium_monthly" }'
```
