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
            WriteId(writer, "reasonId", value.ReasonId);
            WriteId(writer, "previousCustomerId", value.PreviousCustomerId);
            if (value.PauseReason is { } pauseReason)
            {
                writer.WriteString("pauseReason", pauseReason.ToString());
            }

            if (value.Handoff is { } handoff)
            {
                WriteHandoff(writer, handoff);
            }

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

            // Un motivo desconocido (de una versión futura) se lee como null: el hilo no puede fallar por eso.
            AutomationPauseReason? pauseReason = root.TryGetProperty("pauseReason", out var reason) && reason.ValueKind == JsonValueKind.String
                && Enum.TryParse<AutomationPauseReason>(reason.GetString(), ignoreCase: false, out var parsedReason) && Enum.IsDefined(parsedReason)
                ? parsedReason : null;

            return new ConversationEvent(
                parsed, ReadId(root, "actor"), ReadId(root, "target"), ReadId(root, "previous"), ReadId(root, "customerId"),
                ReadId(root, "linkedConversationId"), ReadId(root, "reasonId"), pauseReason, ReadId(root, "previousCustomerId"),
                ReadHandoff(root));
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

    /// <summary>Spec 2026-10-24 §5.2: las claves <c>null</c> se omiten, como los ids.</summary>
    private static void WriteHandoff(Utf8JsonWriter writer, AutomationHandoff handoff)
    {
        writer.WriteStartObject("handoff");
        writer.WriteString("kind", handoff.Kind.ToString());
        WriteText(writer, "code", handoff.Code);
        WriteText(writer, "nodeId", handoff.NodeId);
        WriteText(writer, "nodeTitle", handoff.NodeTitle);
        WriteText(writer, "connectionName", handoff.ConnectionName);
        WriteText(writer, "connectionReason", handoff.ConnectionReason);
        if (handoff.Http is { } http)
        {
            writer.WriteStartObject("http");
            WriteText(writer, "nodeId", http.NodeId);
            WriteText(writer, "nodeTitle", http.NodeTitle);
            WriteText(writer, "outcome", http.Outcome);
            WriteText(writer, "errorCode", http.ErrorCode);
            if (http.Status is { } status)
            {
                writer.WriteNumber("status", status);
            }

            WriteText(writer, "connectionName", http.ConnectionName);
            writer.WriteEndObject();
        }

        // Spec 2026-10-25 D-8: a quién quedó asignada la conversación; interna, el frontend nunca la lee.
        WriteId(writer, "assignedMemberId", handoff.AssignedMemberId);
        writer.WriteEndObject();
    }

    private static void WriteText(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is not null)
        {
            writer.WriteString(name, value);
        }
    }

    /// <summary>Tolerante como <c>pauseReason</c>: un <c>handoff</c> que no es objeto o un <c>kind</c> desconocido dejan la causa en
    /// <c>null</c>; un campo de otro tipo se lee <c>null</c>. Lo leído pasa por <see cref="AutomationHandoff.Create"/>, la misma
    /// limpieza que lo escrito. Nunca tumba el hilo.</summary>
    private static AutomationHandoff? ReadHandoff(JsonElement root)
    {
        if (!root.TryGetProperty("handoff", out var handoff) || handoff.ValueKind != JsonValueKind.Object
            || ReadText(handoff, "kind") is not { } kindName
            || !Enum.TryParse<AutomationHandoffKind>(kindName, ignoreCase: false, out var kind) || !Enum.IsDefined(kind))
        {
            return null;
        }

        AutomationHandoffHttp? http = null;
        if (handoff.TryGetProperty("http", out var element) && element.ValueKind == JsonValueKind.Object)
        {
            int? status = element.TryGetProperty("status", out var statusElement) && statusElement.ValueKind == JsonValueKind.Number
                && statusElement.TryGetInt32(out var parsedStatus)
                    ? parsedStatus
                    : null;
            http = new AutomationHandoffHttp(
                ReadText(element, "nodeId"), ReadText(element, "nodeTitle"), ReadText(element, "outcome"),
                ReadText(element, "errorCode"), status, ReadText(element, "connectionName"));
        }

        return AutomationHandoff.Create(
            kind, ReadText(handoff, "code"), ReadText(handoff, "nodeId"), ReadText(handoff, "nodeTitle"),
            ReadText(handoff, "connectionName"), ReadText(handoff, "connectionReason"), http, ReadId(handoff, "assignedMemberId"));
    }

    private static string? ReadText(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
