using System.Text;
using System.Text.Json;
using Modules.Messaging.Domain;

namespace Modules.Messaging.Application;

/// <summary>Spec 2026-10-10 §6.1.5: la única forma de escribir y leer el <c>details</c> de un evento. La ingesta (SQL,
/// Infrastructure) y los handlers (EF) escriben con <see cref="Serialize"/>; la lectura del hilo usa <see cref="Parse"/>.</summary>
public static class ConversationEventJson
{
    public static string Serialize(ConversationEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", value.Type.ToString());
            WriteId(writer, "actor", value.Actor);
            WriteId(writer, "target", value.Target);
            WriteId(writer, "previous", value.Previous);
            WriteId(writer, "customerId", value.CustomerId);
            WriteId(writer, "linkedConversationId", value.LinkedConversationId);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary><c>null</c> si no es un objeto con un <c>type</c> conocido. Un id ilegible se omite.</summary>
    public static ConversationEvent? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String
                || !Enum.TryParse<ConversationEventType>(type.GetString(), ignoreCase: false, out var parsed)
                || !Enum.IsDefined(parsed))
            {
                return null;
            }

            return new ConversationEvent(
                parsed, ReadId(root, "actor"), ReadId(root, "target"), ReadId(root, "previous"), ReadId(root, "customerId"), ReadId(root, "linkedConversationId"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void WriteId(Utf8JsonWriter writer, string name, Guid? value)
    {
        if (value is { } id)
        {
            writer.WriteString(name, id.ToString("D"));
        }
    }

    private static Guid? ReadId(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String && element.TryGetGuid(out var id) ? id : null;
}
