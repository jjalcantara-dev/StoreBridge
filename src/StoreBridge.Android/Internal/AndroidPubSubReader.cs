using System.Text;
using System.Text.Json;

namespace StoreBridge.Android.Internal;

internal static class AndroidPubSubReader
{
    /// <summary>
    /// Decodes a Pub/Sub push message body. Returns the inner <see cref="DeveloperNotification"/>
    /// and the envelope's <c>messageId</c> for idempotency.
    /// Throws <see cref="ArgumentException"/> for empty input and <see cref="FormatException"/> for malformed payloads.
    /// </summary>
    internal static (DeveloperNotification Notification, string MessageId) Parse(string rawBody)
    {
        if (string.IsNullOrWhiteSpace(rawBody))
            throw new ArgumentException("Webhook body cannot be empty.", nameof(rawBody));

        try
        {
            var pushMessage = JsonSerializer.Deserialize<PubSubPushMessage>(rawBody)
                ?? throw new FormatException("Failed to deserialize Pub/Sub message.");

            if (string.IsNullOrEmpty(pushMessage.Message?.Data))
                throw new FormatException("Pub/Sub message.data is empty.");

            var json = Encoding.UTF8.GetString(Convert.FromBase64String(pushMessage.Message.Data));

            var notification = JsonSerializer.Deserialize<DeveloperNotification>(json)
                ?? throw new FormatException("Failed to deserialize DeveloperNotification payload.");

            return (notification, pushMessage.Message.MessageId ?? string.Empty);
        }
        catch (JsonException ex)
        {
            throw new FormatException($"Pub/Sub message is not valid JSON: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Converts a Unix millisecond timestamp to <see cref="DateTimeOffset"/>, wrapping out-of-range
    /// values into <see cref="FormatException"/>.
    /// </summary>
    internal static DateTimeOffset? FromUnixTimeMilliseconds(long? millis)
    {
        if (!millis.HasValue)
            return null;

        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(millis.Value);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new FormatException($"Unix timestamp '{millis.Value}' is out of range.", ex);
        }
    }
}
