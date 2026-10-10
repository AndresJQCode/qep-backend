using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Cuerpos de webhook con la forma literal de Meta
/// (<c>{"object":"whatsapp_business_account","entry":[{"id":"&lt;waba&gt;","changes":[{"field":…,"value":{…}}]}]}</c>).
/// Se arman con <see cref="JsonSerializer"/> sobre objetos anónimos para no pelear con escapes. Meta
/// manda el <c>timestamp</c> como texto con los segundos Unix.</summary>
internal static class MetaPayloads
{
    /// <summary>La WABA de las pruebas, la misma del <c>SignupBody</c> de Integrations.</summary>
    public const string DefaultWabaId = "222";

    public static string InboundText(
        string phoneNumberId,
        string waId,
        string wamid,
        long timestamp,
        string text,
        string profileName = "Laura",
        string displayPhoneNumber = "15550000000") =>
        Messages(
            phoneNumberId,
            displayPhoneNumber,
            waId,
            profileName,
            new { from = waId, id = wamid, timestamp = Seconds(timestamp), type = "text", text = new { body = text } });

    public static string InboundMedia(
        string phoneNumberId,
        string waId,
        string wamid,
        long timestamp,
        string type,
        string mediaId,
        string mimeType,
        string? caption = null,
        string? filename = null)
    {
        var media = new JsonObject { ["id"] = mediaId, ["mime_type"] = mimeType, ["sha256"] = "c2hhMjU2" };
        if (caption is not null)
        {
            media["caption"] = caption;
        }

        if (filename is not null)
        {
            media["filename"] = filename;
        }

        var message = new JsonObject
        {
            ["from"] = waId,
            ["id"] = wamid,
            ["timestamp"] = Seconds(timestamp),
            ["type"] = type,
            [type] = media,
        };
        return Messages(phoneNumberId, "15550000000", waId, "Laura", message);
    }

    /// <summary>El <c>value</c> de un <c>messages</c> con un mensaje <c>location</c>, para pasarlo a
    /// <see cref="Change"/>.</summary>
    public static string LocationValue(
        string phoneNumberId,
        string waId,
        string wamid,
        long timestamp,
        double latitude,
        double longitude,
        string? name = null,
        string? address = null,
        string profileName = "Laura")
    {
        var location = new JsonObject { ["latitude"] = latitude, ["longitude"] = longitude };
        if (name is not null)
        {
            location["name"] = name;
        }

        if (address is not null)
        {
            location["address"] = address;
        }

        var value = new
        {
            messaging_product = "whatsapp",
            metadata = Metadata(phoneNumberId, "15550000000"),
            contacts = new[] { new { profile = new { name = profileName }, wa_id = waId } },
            messages = new[]
            {
                new JsonObject
                {
                    ["from"] = waId,
                    ["id"] = wamid,
                    ["timestamp"] = Seconds(timestamp),
                    ["type"] = "location",
                    ["location"] = location,
                },
            },
        };
        return JsonSerializer.Serialize(value);
    }

    public static string Status(
        string phoneNumberId,
        string wamid,
        string status,
        long timestamp,
        string? callbackData = null,
        int? errorCode = null,
        string? errorTitle = null)
    {
        var entry = new JsonObject
        {
            ["id"] = wamid,
            ["status"] = status,
            ["timestamp"] = Seconds(timestamp),
            ["recipient_id"] = "573001234567",
        };
        if (callbackData is not null)
        {
            entry["biz_opaque_callback_data"] = callbackData;
        }

        if (errorCode is { } code)
        {
            entry["errors"] = new JsonArray(new JsonObject { ["code"] = code, ["title"] = errorTitle ?? string.Empty });
        }

        var value = new JsonObject
        {
            ["messaging_product"] = "whatsapp",
            ["metadata"] = Metadata(phoneNumberId, "15550000000"),
            ["statuses"] = new JsonArray(entry),
        };
        return Change("messages", value.ToJsonString());
    }

    public static string AccountUpdate(string wabaId, string @event, string? banState = null)
    {
        var value = new JsonObject { ["event"] = @event };
        if (banState is not null)
        {
            value["ban_info"] = new JsonObject { ["waba_ban_state"] = banState, ["waba_ban_date"] = "2026-10-09" };
        }

        return Change("account_update", value.ToJsonString(), wabaId);
    }

    /// <summary>Un cambio cualquiera con su <c>value</c> crudo: para campos que la ingesta ignora o para
    /// armar casos raros a mano.</summary>
    public static string Change(string field, string valueJson, string wabaId = DefaultWabaId) =>
        JsonSerializer.Serialize(new
        {
            @object = "whatsapp_business_account",
            entry = new[]
            {
                new
                {
                    id = wabaId,
                    changes = new[] { new { field, value = JsonNode.Parse(valueJson) } },
                },
            },
        });

    private static string Messages(string phoneNumberId, string displayPhoneNumber, string waId, string profileName, object message)
    {
        var value = new
        {
            messaging_product = "whatsapp",
            metadata = Metadata(phoneNumberId, displayPhoneNumber),
            contacts = new[] { new { profile = new { name = profileName }, wa_id = waId } },
            messages = new[] { message },
        };
        return Change("messages", JsonSerializer.Serialize(value));
    }

    private static JsonObject Metadata(string phoneNumberId, string displayPhoneNumber) =>
        new() { ["display_phone_number"] = displayPhoneNumber, ["phone_number_id"] = phoneNumberId };

    private static string Seconds(long timestamp) => timestamp.ToString(CultureInfo.InvariantCulture);
}
