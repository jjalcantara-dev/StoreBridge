using StoreBridge.Apple.Tests.Fixtures;
using System.Text;

namespace StoreBridge.Apple.Tests;

/// <summary>
/// Seeded fuzzing of everything that touches untrusted webhook bodies. Whatever the input, only the
/// documented exception types may escape — anything else becomes a 500 in a typical handler, and Apple
/// keeps redelivering notifications that fail with 5xx.
/// </summary>
public sealed class AppleWebhookFuzzTests
{
    private static readonly Type[] Allowed =
        [typeof(WebhookAuthenticationException), typeof(FormatException), typeof(ArgumentException)];

    public static IEnumerable<object[]> HandPickedInputs() =>
        new[]
        {
            "{", "{\"signedPayload\":", "{\"signedPayload\":null}", "{\"signedPayload\":\"\\ud800\"}",
            B64Json("{\"alg\":\"\\udc00\",\"x5c\":[]}") + ".e30.AA", B64Json("{\"alg\":\"ES256\",\"x5c\":[\"\\ud800\",\"a\",\"b\"]}") + ".e30.AA", "{\"signedPayload\":123}", "{\"signedPayload\":{}}",
            "{\"other\":1}", "{}  x", "a.b.c", "...", "x", B64Json("{}") + ".e30.AA",
            B64Json("""{"alg":"ES256","x5c":["AAAA","BBBB","CCCC"]}""") + ".e30." + B64(new byte[64]),
            B64Json("""{"alg":"ES256","x5c":[1,2,3]}""") + ".e30.AA",
            B64Json("""{"alg":"ES256","x5c":"nope"}""") + ".e30.AA",
            B64Json("[]") + ".e30.AA",
            WithPayload("""{"data":"str"}"""), WithPayload("[]"), WithPayload("""{"signedDate":"yesterday"}"""),
            WithPayload("""{"data":{"signedTransactionInfo":"a.b"}}""")
        }.Select(s => new object[] { s });

    [Theory]
    [MemberData(nameof(HandPickedInputs))]
    public async Task HandPickedMalformedInputs_OnlyThrowDocumentedExceptions(string body) => await AssertOnlyAllowed(body);

    [Fact]
    public async Task RandomMutationsOfARealNotification_OnlyThrowDocumentedExceptions()
    {
        var rnd = new Random(20261125);
        var seeds = new[] { RealAppleNotifications.SandboxTestNotification, $$"""{"signedPayload":"{{RealAppleNotifications.SandboxTestNotification}}"}""" };
        var symbols = new[] { '.', '"', '{', '}', '=', '\\', '[', ':' };

        for (var i = 0; i < 1500; i++)
        {
            var s = new StringBuilder(seeds[rnd.Next(seeds.Length)]);
            for (var m = rnd.Next(1, 6); m > 0 && s.Length > 0; m--)
            {
                var pos = rnd.Next(s.Length);
                switch (rnd.Next(3))
                {
                    case 0: s[pos] = (char)rnd.Next(32, 127); break;
                    case 1: s.Length = pos; break;
                    default: s.Insert(pos, symbols[rnd.Next(symbols.Length)]); break;
                }
            }

            await AssertOnlyAllowed(s.Length == 0 ? "x" : s.ToString());
        }
    }

    private static async Task AssertOnlyAllowed(string body)
    {
        using var authenticator = new AppleWebhookAuthenticator(new AppleWebhookAuthenticatorOptions { BundleId = RealAppleNotifications.BundleId });
        Func<Task>[] targets =
        [
            () => authenticator.ValidateAsync(body),
            () => new AppleWebhookParser().ParseAsync(body),
            () => new AppleInAppPurchaseWebhookParser().ParseAsync(body)
        ];

        foreach (var target in targets)
        {
            try { await target(); }
            catch (Exception ex) when (!Allowed.Contains(ex.GetType()))
            {
                Assert.Fail($"Unexpected {ex.GetType().FullName} for input [{body[..Math.Min(80, body.Length)]}…]: {ex.Message}");
            }
            catch
            {
                // documented exception type — fine
            }
        }
    }

    private static string WithPayload(string json)
    {
        var parts = RealAppleNotifications.SandboxTestNotification.Split('.');
        return $"{parts[0]}.{B64Json(json)}.{parts[2]}";
    }

    private static string B64Json(string json) => B64(Encoding.UTF8.GetBytes(json));

    private static string B64(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
