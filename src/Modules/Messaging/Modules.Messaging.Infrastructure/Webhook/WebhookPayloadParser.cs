using System.Globalization;
using System.Text.Json;
using Modules.Messaging.Domain;

namespace Modules.Messaging.Infrastructure.Webhook;

/// <summary>
/// Del JSON de Meta a <see cref="WebhookChange"/>. Tolerante a propósito (Review Focus 1): lo que no tiene
/// la forma documentada se salta y lo desconocido cae en <c>Unsupported</c>; nunca lanza. El cuerpo
/// ya pasó la firma, pero eso no lo hace bien formado.
/// </summary>
internal static class WebhookPayloadParser
{
    public const string CallbackPrefix = "qep:";

    // Rango que acepta DateTimeOffset.FromUnixTimeSeconds; fuera de él lanzaría ArgumentOutOfRangeException.
    private const long MinUnixSeconds = -62_135_596_800;
    private const long MaxUnixSeconds = 253_402_300_799;

    public static IReadOnlyList<WebhookChange> Parse(string json)
    {
        var changes = new List<WebhookChange>();
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return changes;
        }

        using (document)
        {
            if (!TryArray(document.RootElement, "entry", out var entries))
            {
                return changes;
            }

            foreach (var entry in entries.EnumerateArray())
            {
                var wabaId = ReadString(entry, "id");
                if (!TryArray(entry, "changes", out var entryChanges))
                {
                    continue;
                }

                foreach (var change in entryChanges.EnumerateArray())
                {
                    var field = ReadString(change, "field") ?? string.Empty;
                    var value = TryObject(change, "value", out var found) ? found : default;
                    changes.Add(field switch
                    {
                        "messages" => ParseMessages(value),
                        "account_update" => ParseAccountUpdate(wabaId ?? string.Empty, value),
                        _ => new UnknownChange(field),
                    });
                }
            }
        }

        return changes;
    }

    private static MessagesChange ParseMessages(JsonElement value)
    {
        var phoneNumberId = TryObject(value, "metadata", out var metadata)
            ? ReadString(metadata, "phone_number_id") ?? string.Empty
            : string.Empty;
        var names = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (TryArray(value, "contacts", out var contacts))
        {
            foreach (var contact in contacts.EnumerateArray())
            {
                if (ReadString(contact, "wa_id") is { } waId)
                {
                    names[waId] = TryObject(contact, "profile", out var profile) ? ReadString(profile, "name") : null;
                }
            }
        }

        var messages = new List<InboundMessage>();
        if (TryArray(value, "messages", out var items))
        {
            foreach (var item in items.EnumerateArray())
            {
                if (ParseMessage(item, names) is { } message)
                {
                    messages.Add(message);
                }
            }
        }

        var statuses = new List<StatusUpdate>();
        if (TryArray(value, "statuses", out var statusItems))
        {
            foreach (var item in statusItems.EnumerateArray())
            {
                if (ParseStatus(item) is { } status)
                {
                    statuses.Add(status);
                }
            }
        }

        return new MessagesChange(phoneNumberId, messages, statuses);
    }

    private static InboundMessage? ParseMessage(JsonElement item, Dictionary<string, string?> names)
    {
        var wamid = ReadString(item, "id");
        var from = ReadString(item, "from");
        if (wamid is null || from is null || !from.All(char.IsAsciiDigit) || ReadTimestamp(item) is not { } occurredAt)
        {
            return null;
        }

        var type = ReadString(item, "type");
        var kind = MessageKindMap.FromMetaType(type);
        var profileName = names.GetValueOrDefault(from);
        string? text = null;
        string? caption = null;
        string? details = null;
        InboundMedia? media = null;

        if (MessageKindMap.HasMedia(kind) && type is not null && TryObject(item, type, out var mediaElement))
        {
            caption = Truncate(ReadString(mediaElement, "caption"), 1024);
            var mediaId = ReadString(mediaElement, "id");
            if (mediaId is not null)
            {
                media = new InboundMedia(mediaId, ReadString(mediaElement, "mime_type") ?? "application/octet-stream", ReadString(mediaElement, "sha256"), ReadString(mediaElement, "filename"));
            }
        }
        else
        {
            switch (kind)
            {
                case MessageKind.Text:
                    text = TryObject(item, "text", out var textElement) ? ReadString(textElement, "body") : null;
                    break;
                case MessageKind.Location when TryObject(item, "location", out var location):
                    details = location.GetRawText();
                    break;
                case MessageKind.Contacts when TryArray(item, "contacts", out var contactList):
                    text = string.Join(", ", contactList.EnumerateArray()
                        .Select(contact => TryObject(contact, "name", out var name) ? ReadString(name, "formatted_name") : null)
                        .Where(name => !string.IsNullOrWhiteSpace(name)));
                    details = contactList.GetRawText();
                    break;
                case MessageKind.Reaction when TryObject(item, "reaction", out var reaction):
                    text = ReadString(reaction, "emoji");
                    details = reaction.GetRawText();
                    break;
                case MessageKind.Interactive when type == "interactive" && TryObject(item, "interactive", out var interactive):
                    text = TryObject(interactive, "button_reply", out var buttonReply) ? ReadString(buttonReply, "title")
                        : TryObject(interactive, "list_reply", out var listReply) ? ReadString(listReply, "title")
                        : null;
                    details = interactive.GetRawText();
                    break;
                case MessageKind.Interactive when type == "button" && TryObject(item, "button", out var button):
                    text = ReadString(button, "text");
                    details = button.GetRawText();
                    break;
                case MessageKind.Unsupported:
                    details = JsonSerializer.Serialize(new
                    {
                        type,
                        errors = item.TryGetProperty("errors", out var errors) ? errors.Clone() : (JsonElement?)null,
                    });
                    break;
                default:
                    break;
            }
        }

        return new InboundMessage(wamid, from, Truncate(profileName, 256), occurredAt, kind, text, caption, details, media);
    }

    private static StatusUpdate? ParseStatus(JsonElement item)
    {
        var wamid = ReadString(item, "id");
        var status = ReadString(item, "status") switch
        {
            "sent" => MessageStatus.Sent,
            "delivered" => MessageStatus.Delivered,
            "read" or "played" => MessageStatus.Read, // D-M9
            "failed" => MessageStatus.Failed,
            _ => (MessageStatus?)null,
        };
        if (wamid is null || status is null || ReadTimestamp(item) is not { } occurredAt)
        {
            return null;
        }

        Guid? callback = null;
        if (ReadString(item, "biz_opaque_callback_data") is { } data && data.StartsWith(CallbackPrefix, StringComparison.Ordinal)
            && Guid.TryParseExact(data.AsSpan(CallbackPrefix.Length), "D", out var parsed))
        {
            callback = parsed;
        }

        int? errorCode = null;
        string? errorTitle = null;
        if (TryArray(item, "errors", out var errors) && errors.GetArrayLength() > 0)
        {
            var first = errors[0];
            errorCode = first.ValueKind == JsonValueKind.Object && first.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var parsedCode) ? parsedCode : null;
            errorTitle = ReadString(first, "title");
        }

        return new StatusUpdate(wamid, status.Value, occurredAt, callback, errorCode, errorTitle);
    }

    private static AccountUpdateChange ParseAccountUpdate(string wabaId, JsonElement value)
    {
        var @event = ReadString(value, "event") ?? string.Empty;
        var banState = TryObject(value, "ban_info", out var ban) ? ReadString(ban, "waba_ban_state") : null;
        return new AccountUpdateChange(wabaId, @event, banState);
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("timestamp", out var timestamp))
        {
            return null;
        }

        var seconds = timestamp.ValueKind switch
        {
            JsonValueKind.String when long.TryParse(timestamp.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) => parsed,
            JsonValueKind.Number when timestamp.TryGetInt64(out var number) => number,
            _ => (long?)null,
        };
        return seconds is >= MinUnixSeconds and <= MaxUnixSeconds ? DateTimeOffset.FromUnixTimeSeconds(seconds.Value) : null;
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

    private static string? Truncate(string? value, int maxLength) =>
        value is null ? null : value.Length <= maxLength ? value : value[..maxLength];
}
