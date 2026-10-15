using System.Globalization;
using System.Text.Json;
using Modules.Messaging.Domain;

namespace Modules.Messaging.Infrastructure.Webhook;

/// <summary>
/// Spec 2026-10-14 §6.2: del JSON de Instagram (<c>object: "instagram"</c>, <c>entry[].messaging[]</c>) a <see cref="InstagramEntry"/>.
/// Tolerante como <see cref="WebhookPayloadParser"/>: lo que no tiene la forma documentada cae como <see cref="InstagramIgnoredEvent"/> y
/// lo desconocido en <c>Unsupported</c>; nunca lanza. El <c>timestamp</c> viene en milisegundos (en WhatsApp, en segundos).
/// </summary>
internal static class InstagramWebhookPayloadParser
{
    /// <summary>Una reacción no trae <c>mid</c> propio: su clave es la del mensaje reaccionado más el instante, para que un reenvío de
    /// Meta no la duplique (<c>IX_messages_connection_wamid</c>).</summary>
    public const string ReactionWamidPrefix = "reaction:";

    public const string PostbackWamidPrefix = "postback:";

    public static IReadOnlyList<InstagramEntry> Parse(string json)
    {
        var entries = new List<InstagramEntry>();
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return entries;
        }

        using (document)
        {
            if (!TryArray(document.RootElement, "entry", out var items))
            {
                return entries;
            }

            foreach (var entry in items.EnumerateArray())
            {
                var accountId = ReadString(entry, "id");
                if (accountId is null || !TryArray(entry, "messaging", out var messaging))
                {
                    continue;
                }

                var events = new List<InstagramEvent>();
                foreach (var item in messaging.EnumerateArray())
                {
                    events.Add(ParseEvent(accountId, item));
                }

                entries.Add(new InstagramEntry(accountId, events));
            }
        }

        return entries;
    }

    private static InstagramEvent ParseEvent(string accountId, JsonElement item)
    {
        var sender = TryObject(item, "sender", out var senderElement) ? ReadString(senderElement, "id") : null;
        var recipient = TryObject(item, "recipient", out var recipientElement) ? ReadString(recipientElement, "id") : null;
        if (ReadTimestamp(item) is not { } occurredAt)
        {
            return new InstagramIgnoredEvent("no_timestamp");
        }

        if (TryObject(item, "message", out var message))
        {
            var isEcho = message.TryGetProperty("is_echo", out var echo) && echo.ValueKind == JsonValueKind.True;
            // El echo lo emite la cuenta; la persona es el receptor. En el resto la persona es el emisor.
            var userId = isEcho ? recipient : sender;
            if (!Conversation.IsValidInstagramUserId(userId) || string.Equals(userId, accountId, StringComparison.Ordinal))
            {
                return new InstagramIgnoredEvent("no_user_id");
            }

            if (ReadString(message, "mid") is not { } mid)
            {
                return new InstagramIgnoredEvent("no_mid");
            }

            if (message.TryGetProperty("is_deleted", out var deleted) && deleted.ValueKind == JsonValueKind.True)
            {
                return new InstagramDeletedEvent(userId!, mid);
            }

            return new InstagramMessageEvent(ParseMessage(userId!, mid, occurredAt, message), isEcho);
        }

        if (!Conversation.IsValidInstagramUserId(sender) || string.Equals(sender, accountId, StringComparison.Ordinal))
        {
            return new InstagramIgnoredEvent("no_user_id");
        }

        if (TryObject(item, "read", out var read))
        {
            return ReadString(read, "mid") is { } mid ? new InstagramSeenEvent(sender!, mid) : new InstagramIgnoredEvent("no_mid");
        }

        if (TryObject(item, "reaction", out var reaction))
        {
            if (ReadString(reaction, "mid") is not { } mid)
            {
                return new InstagramIgnoredEvent("no_mid");
            }

            if (!string.Equals(ReadString(reaction, "action"), "react", StringComparison.Ordinal))
            {
                // Quitar la reacción no se refleja (D-I5): el hilo conserva la que hubo.
                return new InstagramIgnoredEvent("unreact");
            }

            var text = ReadString(reaction, "emoji") ?? ReadString(reaction, "reaction");
            var wamid = $"{ReactionWamidPrefix}{mid}:{occurredAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}";
            return new InstagramMessageEvent(
                new InboundMessage(wamid, sender!, null, null, null, null, occurredAt, MessageKind.Reaction, text, null, reaction.GetRawText(), null, mid),
                IsEcho: false);
        }

        if (TryObject(item, "postback", out var postback))
        {
            var mid = ReadString(postback, "mid") ?? $"{PostbackWamidPrefix}{occurredAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}";
            var title = ReadString(postback, "title");
            return new InstagramMessageEvent(
                new InboundMessage(mid, sender!, null, null, null, null, occurredAt, MessageKind.Interactive, title, null, postback.GetRawText(), null, null,
                    new QuickReplyButton(title ?? string.Empty, ReadString(postback, "payload"))),
                IsEcho: false);
        }

        return new InstagramIgnoredEvent("unknown_event");
    }

    /// <summary>§6.2, el mapa: texto; <c>image|video|audio|file</c> con su URL; <c>share|ig_post|ig_reel|reel|post</c> como Share con la
    /// URL de texto; el resto, Unsupported con el adjunto crudo. La cita (<c>reply_to.mid</c>) y la historia respondida
    /// (<c>reply_to.story</c>, en details) acompañan a cualquier kind.</summary>
    private static InboundMessage ParseMessage(string userId, string mid, DateTimeOffset occurredAt, JsonElement message)
    {
        var text = ReadString(message, "text");
        string? quotedWamid = null;
        string? details = null;
        if (TryObject(message, "reply_to", out var replyTo))
        {
            quotedWamid = ReadString(replyTo, "mid");
            if (TryObject(replyTo, "story", out var story))
            {
                details = JsonSerializer.Serialize(new { story = story.Clone() });
            }
        }

        var kind = text is not null ? MessageKind.Text : MessageKind.Unsupported;
        InboundMedia? media = null;
        if (TryArray(message, "attachments", out var attachments) && attachments.GetArrayLength() > 0)
        {
            var first = attachments[0];
            var type = ReadString(first, "type");
            var url = TryObject(first, "payload", out var payload) ? ReadString(payload, "url") : null;
            switch (type)
            {
                case "image" or "video" or "audio" or "file" when url is not null:
                    kind = type switch
                    {
                        "image" => MessageKind.Image,
                        "video" => MessageKind.Video,
                        "audio" => MessageKind.Audio,
                        _ => MessageKind.Document,
                    };
                    // Sin mime en el webhook: lo pone la copia, con el Content-Type de la descarga.
                    media = new InboundMedia(string.Empty, GuessMimeType(type), null, null, url);
                    break;
                case "share" or "ig_post" or "ig_reel" or "reel" or "post" or "story_mention" or "ig_story" when url is not null:
                    kind = MessageKind.Share;
                    text = url;
                    break;
                default:
                    kind = MessageKind.Unsupported;
                    details = JsonSerializer.Serialize(new { type, attachment = first.Clone() });
                    break;
            }
        }

        return new InboundMessage(mid, userId, null, null, null, null, occurredAt, kind, text, null, details, media, quotedWamid);
    }

    private static string GuessMimeType(string type) => type switch
    {
        "image" => "image/jpeg",
        "video" => "video/mp4",
        "audio" => "audio/mp4",
        _ => "application/octet-stream",
    };

    // Rango que acepta DateTimeOffset.FromUnixTimeMilliseconds.
    private const long MinUnixMilliseconds = -62_135_596_800_000;
    private const long MaxUnixMilliseconds = 253_402_300_799_999;

    private static DateTimeOffset? ReadTimestamp(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("timestamp", out var timestamp))
        {
            return null;
        }

        var milliseconds = timestamp.ValueKind switch
        {
            JsonValueKind.String when long.TryParse(timestamp.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) => parsed,
            JsonValueKind.Number when timestamp.TryGetInt64(out var number) => number,
            _ => (long?)null,
        };
        return milliseconds is >= MinUnixMilliseconds and <= MaxUnixMilliseconds ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds.Value) : null;
    }

    private static bool TryArray(JsonElement element, string property, out JsonElement array)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out array) && array.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        array = default;
        return false;
    }

    private static bool TryObject(JsonElement element, string property, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out value) && value.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        value = default;
        return false;
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
