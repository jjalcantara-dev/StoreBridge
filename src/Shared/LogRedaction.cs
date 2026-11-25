namespace StoreBridge.Internal;

/// <summary>
/// Masks purchase identifiers before they reach log sinks. Google purchase tokens act as credentials
/// for the purchase (anyone with the developer API access and the token can query, acknowledge or
/// consume it) and Apple transaction IDs identify a customer's purchase history, so logs keep only
/// enough characters to correlate entries.
/// </summary>
/// <remarks>Compiled into each platform package via a linked source file (see the .csproj files).</remarks>
internal static class LogRedaction
{
    private const int Visible = 4;

    /// <summary>
    /// Returns <c>"abcd…wxyz (len 150)"</c> for long values, <c>"***"</c> for short ones and
    /// <c>"(empty)"</c> for null/empty input.
    /// </summary>
    internal static string Token(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "(empty)";

        if (value.Length <= Visible * 3)
            return "***";

        return $"{value[..Visible]}…{value[^Visible..]} (len {value.Length})";
    }
}
