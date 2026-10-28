using System.Text.Json;
using Modules.Shopify.Application;

namespace Modules.Shopify.Infrastructure.Parsing;

/// <summary>El puerto de Application sobre <see cref="RestOrderParser"/>. Un cuerpo que ni siquiera es JSON también es un fallo
/// permanente (<see cref="ShopifyPayloadException"/>), no un error que el worker reintente.</summary>
internal sealed class ShopifyOrderPayloadParser : IShopifyOrderPayloadParser
{
    public ShopifyOrderSnapshot ParseRest(string payloadJson)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payloadJson);
        }
        catch (JsonException exception)
        {
            throw new ShopifyPayloadException("The Shopify order payload is not valid JSON.", exception);
        }

        using (document)
        {
            return RestOrderParser.Parse(document.RootElement);
        }
    }
}
