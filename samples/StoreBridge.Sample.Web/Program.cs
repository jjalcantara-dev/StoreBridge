using System.Text.Json.Serialization;
using StoreBridge;
using StoreBridge.Android;
using StoreBridge.Apple;

// Sample host that doubles as a real-payload validation harness:
// point Apple's `requestTestNotification` and Google Play's "Send test notification"
// at the /webhooks/* endpoints and inspect what the library parsed.
var builder = WebApplication.CreateBuilder(args);

// Render enums as names instead of numbers in the JSON responses.
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// Apple — bind from the "Apple" configuration section (use user-secrets for the real values).
builder.Services.AddAppleSubscriptions(builder.Configuration.GetSection("Apple"));
builder.Services.AddAppleInAppPurchases(builder.Configuration.GetSection("Apple"));
builder.Services.AddAppleWebhooks(builder.Configuration.GetSection("Apple:Webhook"));

// Google — bind from the "Google" configuration section.
builder.Services.AddAndroidSubscriptions(builder.Configuration.GetSection("Google"));
builder.Services.AddAndroidInAppPurchases(builder.Configuration.GetSection("Google"));
builder.Services.AddAndroidWebhooks(builder.Configuration.GetSection("Google:Webhook"));
// Note: the webhook authenticators validate their options on first use — an empty Apple:Webhook:BundleId or
// Google:Webhook:ExpectedServiceAccountEmail fails with an OptionsValidationException explaining what to set.

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new
{
    service = "StoreBridge.Sample.Web",
    endpoints = new[]
    {
        "POST /webhooks/apple",
        "POST /webhooks/google",
        "POST /verify/apple/subscription   { token, productId? }",
        "POST /verify/apple/purchase       { token, productId? }",
        "POST /verify/google/subscription  { token, productId }",
        "POST /verify/google/purchase      { token, productId }"
    }
}));

// ── Webhooks ────────────────────────────────────────────────────────
// Authenticate first, then parse. The parsed notification is returned as JSON
// so you can see exactly what the library extracted from a real payload.

app.MapPost("/webhooks/apple", async (
    HttpRequest request,
    AppleWebhookAuthenticator authenticator,
    AppleWebhookParser parser,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    using var reader = new StreamReader(request.Body, leaveOpen: true);
    var body = await reader.ReadToEndAsync(ct);

    try
    {
        await authenticator.ValidateAsync(body, cancellationToken: ct);
    }
    catch (WebhookAuthenticationException ex)
    {
        logger.LogWarning(ex, "Apple webhook authentication failed");
        return Results.Unauthorized();
    }
    catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException)
    {
        logger.LogWarning(ex, "Apple webhook body is malformed");
        return Results.BadRequest();
    }

    var notification = await parser.ParseAsync(body, ct);
    logger.LogInformation("Apple notification {Id}: {Event} ({Raw})",
        notification.NotificationId, notification.EventType, notification.RawEventType);
    return Results.Ok(notification);
});

app.MapPost("/webhooks/google", async (
    HttpRequest request,
    AndroidWebhookAuthenticator authenticator,
    AndroidWebhookParser subscriptionParser,
    AndroidInAppPurchaseWebhookParser purchaseParser,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    using var reader = new StreamReader(request.Body, leaveOpen: true);
    var body = await reader.ReadToEndAsync(ct);

    try
    {
        await authenticator.ValidateAsync(body, request.Headers.Authorization, ct);
    }
    catch (WebhookAuthenticationException ex)
    {
        logger.LogWarning(ex, "Google webhook authentication failed");
        return Results.Unauthorized();
    }

    // A Pub/Sub message is a subscription, a one-time product, a voided purchase, a refund review, or a test.
    // Try the subscription parser; fall back to the in-app purchase parser.
    try
    {
        var notification = await subscriptionParser.ParseAsync(body);
        logger.LogInformation("Google subscription notification {Id}: {Event}",
            notification.NotificationId, notification.EventType);
        return Results.Ok(notification);
    }
    catch (FormatException)
    {
        try
        {
            var notification = await purchaseParser.ParseAsync(body);
            logger.LogInformation("Google purchase notification {Id}: {Event}",
                notification.NotificationId, notification.EventType);
            return Results.Ok(notification);
        }
        catch (FormatException ex)
        {
            // Unknown or future notification kind: acknowledge it (2xx) so Pub/Sub doesn't redeliver it forever.
            logger.LogWarning(ex, "Unrecognized Google notification acknowledged without processing");
            return Results.Ok();
        }
    }
});

// ── Verification ────────────────────────────────────────────────────

app.MapPost("/verify/apple/subscription", async (
    VerifyRequest req, AppleSubscriptionVerifier verifier) =>
{
    var result = await verifier.VerifySubscriptionAsync(req.Token, req.ProductId);
    return result.IsVerified ? Results.Ok(result) : Results.UnprocessableEntity(result);
});

app.MapPost("/verify/apple/purchase", async (
    VerifyRequest req, AppleInAppPurchaseVerifier verifier) =>
{
    var result = await verifier.VerifyPurchaseAsync(req.Token, req.ProductId);
    return result.IsVerified ? Results.Ok(result) : Results.UnprocessableEntity(result);
});

app.MapPost("/verify/google/subscription", async (
    VerifyRequest req, AndroidSubscriptionVerifier verifier) =>
{
    var result = await verifier.VerifySubscriptionAsync(req.Token, req.ProductId);
    return result.IsVerified ? Results.Ok(result) : Results.UnprocessableEntity(result);
});

app.MapPost("/verify/google/purchase", async (
    VerifyRequest req, AndroidInAppPurchaseVerifier verifier) =>
{
    var result = await verifier.VerifyPurchaseAsync(req.Token, req.ProductId);
    return result.IsVerified ? Results.Ok(result) : Results.UnprocessableEntity(result);
});

app.Run();

/// <summary>Request body for the verification endpoints.</summary>
/// <param name="Token">Apple: original transaction ID / transaction ID. Google: purchase token.</param>
/// <param name="ProductId">Product or subscription ID. Required for Google, optional for Apple.</param>
internal sealed record VerifyRequest(string Token, string? ProductId);
