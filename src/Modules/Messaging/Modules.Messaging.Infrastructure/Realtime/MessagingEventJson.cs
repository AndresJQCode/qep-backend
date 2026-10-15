using System.Text.Json;
using Modules.Messaging.Application;

namespace Modules.Messaging.Infrastructure.Realtime;

/// <summary>El sobre que viaja por <c>NOTIFY</c>: camelCase (<c>JsonSerializerDefaults.Web</c>) porque <c>kind</c>
/// es el mismo nombre del evento que recibe el navegador, y lo que el listener no entiende se descarta sin tumbar nada.</summary>
internal static class MessagingEventJson
{
    public const string Channel = "messaging_events";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize(MessagingEvent @event) => JsonSerializer.Serialize(@event, Options);

    public static MessagingEvent? TryDeserialize(string payload)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<MessagingEvent>(payload, Options);
            return parsed is { Kind.Length: > 0 } && parsed.TenantId != Guid.Empty && parsed.ConversationId != Guid.Empty ? parsed : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
