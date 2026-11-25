using StoreBridge.Internal;

namespace StoreBridge.Android.Tests;

public sealed class LogRedactionTests
{
    [Theory]
    [InlineData(null, "(empty)")]
    [InlineData("", "(empty)")]
    [InlineData("short", "***")]
    [InlineData("123456789012", "***")]
    [InlineData("2000000123456789", "2000…6789 (len 16)")]
    public void Token_MasksMiddle(string? input, string expected) =>
        Assert.Equal(expected, LogRedaction.Token(input));

    [Fact]
    public void Token_LongPurchaseToken_NeverContainsTheSecretPart()
    {
        var token = "abcd" + new string('x', 140) + "wxyz";
        var redacted = LogRedaction.Token(token);

        Assert.DoesNotContain("xxxx", redacted);
        Assert.Equal("abcd…wxyz (len 148)", redacted);
    }
}
