using System.Text;

namespace StoreBridge.Android.Tests;

/// <summary>Seeded fuzzing of the Pub/Sub parsers: only <see cref="FormatException"/> / <see cref="ArgumentException"/> may escape.</summary>
public sealed class AndroidWebhookFuzzTests
{
    [Fact]
    public async Task RandomMutations_OnlyThrowDocumentedExceptions()
    {
        static string Wrap(string inner) =>
            $$"""{"message":{"data":"{{Convert.ToBase64String(Encoding.UTF8.GetBytes(inner))}}","messageId":"1"},"subscription":"s"}""";

        var seeds = new[]
        {
            Wrap("""{"version":"1.0","packageName":"com.example.app","eventTimeMillis":"1","subscriptionNotification":{"version":"1.0","notificationType":4,"purchaseToken":"t"}}"""),
            Wrap("""{"version":"1.0","packageName":"com.example.app","eventTimeMillis":1,"voidedPurchaseNotification":{"purchaseToken":"t","orderId":"o","productType":2,"refundType":1}}"""),
            Wrap("""{"version":"1.0","packageName":"com.example.app","eventTimeMillis":1,"pendingRefundReviewNotification":{"pendingRefundToken":"r","orderId":"o","refundReason":7}}"""),
            Wrap("""{"subscriptionNotification":{"notificationType":"x"}}"""),
            Wrap("[1,2]"),
            """{"message":{"data":"!!!"}}"""
        };
        var rnd = new Random(20261125);
        var allowed = new[] { typeof(FormatException), typeof(ArgumentException) };
        var parsers = new Func<string, Task>[] { b => new AndroidWebhookParser().ParseAsync(b), b => new AndroidInAppPurchaseWebhookParser().ParseAsync(b) };

        for (var i = 0; i < 2000; i++)
        {
            var s = new StringBuilder(seeds[rnd.Next(seeds.Length)]);
            for (var m = rnd.Next(0, 5); m > 0 && s.Length > 0; m--)
            {
                var pos = rnd.Next(s.Length);
                if (rnd.Next(2) == 0) s[pos] = (char)rnd.Next(32, 127); else s.Length = pos;
            }
            var body = s.Length == 0 ? "x" : s.ToString();

            foreach (var parse in parsers)
            {
                try { await parse(body); }
                catch (Exception ex) when (!allowed.Contains(ex.GetType()))
                {
                    Assert.Fail($"Unexpected {ex.GetType().FullName} for [{body[..Math.Min(80, body.Length)]}]: {ex.Message}");
                }
                catch { }
            }
        }
    }
}
