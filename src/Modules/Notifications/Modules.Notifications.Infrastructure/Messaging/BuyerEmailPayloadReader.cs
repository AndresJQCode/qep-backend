using System.Text.Json;
using Modules.Buyers.Application;

namespace Modules.Notifications.Infrastructure.Messaging;

/// <summary>Lee el payload de los eventos del comprador. Los mensajes de error de System.Text.Json no
/// incluyen valores del documento, así que ni el correo ni el token se filtran por acá.</summary>
internal static class BuyerEmailPayloadReader
{
    public static BuyerEmailPayload Parse(string payloadJson) =>
        JsonSerializer.Deserialize<BuyerEmailPayload>(payloadJson, JsonSerializerOptions.Web)
        ?? throw new JsonException("The buyer email payload is empty.");
}
