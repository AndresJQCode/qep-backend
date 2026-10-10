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

    /// <summary>Spec 2026-10-10 §8.1: Meta manda siempre el BSUID. Las pruebas derivan uno válido y estable del
    /// teléfono, así siguen teniendo «una conversación por número».</summary>
    public const string DefaultUserIdPrefix = "CO.";

    public static string UserIdFor(string waId) => DefaultUserIdPrefix + waId;

    /// <summary>Spec 2026-10-10 §3: un texto entrante con BSUID (siempre), teléfono si <paramref name="waId"/> no es
    /// <c>null</c> (en <c>from</c> y en el contacto), usuario si lo hay y la cita si <paramref name="quotedWamid"/>.</summary>
    public static string Inbound(
        string phoneNumberId, string userId, string? waId, string wamid, long timestamp, string text,
        string? profileName = "Laura", string? username = null, string? quotedWamid = null)
    {
        var message = new JsonObject
        {
            ["from_user_id"] = userId,
            ["id"] = wamid,
            ["timestamp"] = Seconds(timestamp),
            ["type"] = "text",
            ["text"] = new JsonObject { ["body"] = text },
        };
        if (waId is not null)
        {
            message["from"] = waId;
        }

        if (quotedWamid is not null)
        {
            message["context"] = new JsonObject { ["id"] = quotedWamid, ["from"] = "15550000000" };
        }

        var profile = new JsonObject();
        if (profileName is not null)
        {
            profile["name"] = profileName;
        }

        if (username is not null)
        {
            profile["username"] = username;
        }

        var contact = new JsonObject { ["profile"] = profile, ["user_id"] = userId };
        if (waId is not null)
        {
            contact["wa_id"] = waId;
        }

        var value = new JsonObject
        {
            ["messaging_product"] = "whatsapp",
            ["metadata"] = Metadata(phoneNumberId, "15550000000"),
            ["contacts"] = new JsonArray(contact),
            ["messages"] = new JsonArray(message),
        };
        return Change("messages", value.ToJsonString());
    }

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
            UserIdFor(waId),
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
        return Messages(phoneNumberId, "15550000000", UserIdFor(waId), waId, "Laura", message);
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
            contacts = new[] { new { profile = new { name = profileName }, user_id = UserIdFor(waId), wa_id = waId } },
            messages = new[]
            {
                new JsonObject
                {
                    ["from_user_id"] = UserIdFor(waId),
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

    /// <summary>Spec 2026-10-10 §3: la forma de <c>user_id_update</c> no está documentada con un ejemplo; ésta es la que
    /// lee el parser (riesgo de §13).</summary>
    public static string UserIdUpdate(string previous, string current, string? phoneNumberId = "111", string wabaId = DefaultWabaId)
    {
        var value = new JsonObject { ["user_id"] = new JsonObject { ["previous"] = previous, ["current"] = current } };
        if (phoneNumberId is not null)
        {
            value["metadata"] = Metadata(phoneNumberId, "15550000000");
        }

        return Change("user_id_update", value.ToJsonString(), wabaId);
    }

    public static string UserChangedUserId(string phoneNumberId, string fromUserId, string newUserId, string body, string wamid, long timestamp)
    {
        var message = new JsonObject
        {
            ["from_user_id"] = fromUserId,
            ["id"] = wamid,
            ["timestamp"] = Seconds(timestamp),
            ["type"] = "system",
            ["system"] = new JsonObject { ["body"] = body, ["type"] = "user_changed_user_id", ["user_id"] = newUserId },
        };
        var value = new JsonObject
        {
            ["messaging_product"] = "whatsapp",
            ["metadata"] = Metadata(phoneNumberId, "15550000000"),
            ["contacts"] = new JsonArray(new JsonObject { ["profile"] = new JsonObject { ["name"] = "Laura" }, ["user_id"] = newUserId }),
            ["messages"] = new JsonArray(message),
        };
        return Change("messages", value.ToJsonString());
    }

    private static readonly string[] MergedKeys = ["contacts", "messages"];

    /// <summary>Un solo change <c>messages</c> con los mensajes y contactos de <paramref name="first"/> y luego los de
    /// <paramref name="second"/>, en ese orden: como cuando Meta junta varios en la misma entrega.</summary>
    public static string SameChange(string first, string second)
    {
        var root = JsonNode.Parse(first)!;
        var value = root["entry"]![0]!["changes"]![0]!["value"]!.AsObject();
        var other = JsonNode.Parse(second)!["entry"]![0]!["changes"]![0]!["value"]!;
        foreach (var key in MergedKeys)
        {
            var target = value[key]?.AsArray() ?? [];
            value[key] = target;
            foreach (var item in other[key]?.AsArray() ?? [])
            {
                target.Add(item!.DeepClone());
            }
        }

        return root.ToJsonString();
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

    private static string Messages(string phoneNumberId, string displayPhoneNumber, string userId, string? waId, string profileName, object message)
    {
        // Spec 2026-10-10 §8.1: el mensaje lleva su from_user_id (y pierde from si no hay teléfono); el contacto,
        // su user_id y el wa_id sólo si lo hay.
        var node = JsonSerializer.SerializeToNode(message)!.AsObject();
        node["from_user_id"] = userId;
        if (waId is null)
        {
            node.Remove("from");
        }

        var contact = new JsonObject { ["profile"] = new JsonObject { ["name"] = profileName }, ["user_id"] = userId };
        if (waId is not null)
        {
            contact["wa_id"] = waId;
        }

        var value = new JsonObject
        {
            ["messaging_product"] = "whatsapp",
            ["metadata"] = Metadata(phoneNumberId, displayPhoneNumber),
            ["contacts"] = new JsonArray(contact),
            ["messages"] = new JsonArray(node),
        };
        return Change("messages", value.ToJsonString());
    }

    private static JsonObject Metadata(string phoneNumberId, string displayPhoneNumber) =>
        new() { ["display_phone_number"] = displayPhoneNumber, ["phone_number_id"] = phoneNumberId };

    private static string Seconds(long timestamp) => timestamp.ToString(CultureInfo.InvariantCulture);
}
