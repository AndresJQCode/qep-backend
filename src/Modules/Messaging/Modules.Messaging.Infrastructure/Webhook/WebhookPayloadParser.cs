using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Modules.Messaging.Domain;

namespace Modules.Messaging.Infrastructure.Webhook;

/// <summary>
/// Del JSON de Meta a <see cref="WebhookChange"/>. Tolerante a propósito (Review Focus 1): lo que no tiene
/// la forma documentada se salta y lo desconocido cae en <c>Unsupported</c>; nunca lanza. El cuerpo
/// ya pasó la firma, pero eso no lo hace bien formado.
/// </summary>
internal static partial class WebhookPayloadParser
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
                        "user_id_update" => ParseUserIdUpdate(wabaId ?? string.Empty, value),
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
        // Spec 2026-10-10 §8.1: el contacto se indexa por BSUID, que es lo que trae cada mensaje en from_user_id.
        var contactsByUserId = new Dictionary<string, ContactProfile>(StringComparer.Ordinal);
        if (TryArray(value, "contacts", out var contacts))
        {
            foreach (var contact in contacts.EnumerateArray())
            {
                if (ReadString(contact, "user_id") is { } userId && Conversation.IsValidUserId(userId))
                {
                    var hasProfile = TryObject(contact, "profile", out var profile);
                    contactsByUserId[userId] = new ContactProfile(
                        Truncate(hasProfile ? ReadString(profile, "name") : null, Conversation.ProfileNameMaxLength),
                        Truncate(hasProfile ? ReadString(profile, "username") : null, Conversation.UsernameMaxLength),
                        Truncate(ReadString(contact, "parent_user_id"), Conversation.UserIdMaxLength),
                        ValidWaId(ReadString(contact, "wa_id")));
                }
            }
        }

        var messages = new List<InboundMessage>();
        var numberChanges = new List<UserIdChange>();
        var skippedWithoutUserId = 0;
        if (TryArray(value, "messages", out var items))
        {
            foreach (var item in items.EnumerateArray())
            {
                if (IsUserChangedUserId(item))
                {
                    if (ParseNumberChange(item) is { } numberChange)
                    {
                        numberChanges.Add(numberChange);
                    }
                }
                else if (item.ValueKind == JsonValueKind.Object && !Conversation.IsValidUserId(ReadString(item, "from_user_id")))
                {
                    skippedWithoutUserId++;
                }
                else if (ParseMessage(item, contactsByUserId) is { } message)
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

        return new MessagesChange(phoneNumberId, messages, statuses, numberChanges, skippedWithoutUserId);
    }

    private static bool IsUserChangedUserId(JsonElement item) =>
        ReadString(item, "type") == "system" && TryObject(item, "system", out var system) && ReadString(system, "type") == "user_changed_user_id";

    private static InboundMessage? ParseMessage(JsonElement item, Dictionary<string, ContactProfile> contacts)
    {
        var wamid = ReadString(item, "id");
        var userId = ReadString(item, "from_user_id");
        // Spec 2026-10-10 §8.1: el BSUID es la clave y Meta lo manda siempre. Sin él, el mensaje se salta (tolerancia
        // de base: nunca lanza) y ParseMessages lo cuenta para que el procesador lo registre. El teléfono es un dato opcional: si from no tiene la forma, se descarta y el
        // mensaje entra igual; si falta, se toma el wa_id del contacto.
        if (wamid is null || !Conversation.IsValidUserId(userId) || ReadTimestamp(item) is not { } occurredAt)
        {
            return null;
        }

        // IsValidUserId ya descartó el null.
        contacts.TryGetValue(userId!, out var contact);
        var waId = ValidWaId(ReadString(item, "from")) ?? contact?.WaId;
        var quotedWamid = TryObject(item, "context", out var context) ? ReadString(context, "id") : null;
        var type = ReadString(item, "type");
        var kind = MessageKindMap.FromMetaType(type);
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
                // Al ancho de cada columna de message_media: un valor más largo haría fallar el INSERT en cada intento.
                media = new InboundMedia(
                    mediaId,
                    Truncate(ReadString(mediaElement, "mime_type"), 128) ?? "application/octet-stream",
                    Truncate(ReadString(mediaElement, "sha256"), 64),
                    Truncate(ReadString(mediaElement, "filename"), 256));
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

        // El ! va detrás de IsValidUserId, que descarta el null.
        return new InboundMessage(
            wamid, userId!, waId, contact?.Name, contact?.Username, contact?.ParentUserId, occurredAt, kind, text, caption, details, media, quotedWamid);
    }

    private static string? ValidWaId(string? value) =>
        value is { Length: > 0 and <= Conversation.WaIdMaxLength } && value.All(char.IsAsciiDigit) ? value : null;

    [GeneratedRegex("changed from (\\S+) to (\\S+)", RegexOptions.CultureInvariant)]
    private static partial Regex ChangedFrom();

    /// <summary>Spec 2026-10-10 §8.3: el nuevo es <c>system.user_id</c>; el anterior, <c>from_user_id</c> si es distinto,
    /// y si no, el del cuerpo («changed from &lt;OLD&gt; to &lt;NEW&gt;»).</summary>
    private static UserIdChange? ParseNumberChange(JsonElement item)
    {
        if (!TryObject(item, "system", out var system) || ReadString(system, "user_id") is not { } current || !Conversation.IsValidUserId(current))
        {
            return null;
        }

        var previous = ReadString(item, "from_user_id");
        if (previous is null || previous == current)
        {
            var match = ChangedFrom().Match(ReadString(system, "body") ?? string.Empty);
            previous = match.Success ? match.Groups[1].Value : null;
        }

        return Conversation.IsValidUserId(previous) && previous != current
            ? new UserIdChange(previous!, current, ValidWaId(ReadString(system, "wa_id")))
            : null;
    }

    /// <summary>§8.3: la forma no está documentada con un ejemplo (riesgo de §13); se lee <c>user_id.{previous,current}</c>
    /// y, si viene, <c>metadata.phone_number_id</c>. Lo que no tenga esa forma cae como campo ignorado, en el log.</summary>
    private static WebhookChange ParseUserIdUpdate(string wabaId, JsonElement value)
    {
        if (!TryObject(value, "user_id", out var ids)
            || ReadString(ids, "previous") is not { } previous || ReadString(ids, "current") is not { } current
            || !Conversation.IsValidUserId(previous) || !Conversation.IsValidUserId(current) || previous == current)
        {
            return new UnknownChange("user_id_update");
        }

        var phoneNumberId = TryObject(value, "metadata", out var metadata) ? ReadString(metadata, "phone_number_id") : null;
        return new UserIdUpdateChange(wabaId, phoneNumberId, new UserIdChange(previous, current, ValidWaId(ReadString(value, "wa_id"))));
    }

    /// <summary>El contacto de Meta, ya recortado al ancho de cada columna (spec 2026-10-10 §6.1.1).</summary>
    private sealed record ContactProfile(string? Name, string? Username, string? ParentUserId, string? WaId);

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
