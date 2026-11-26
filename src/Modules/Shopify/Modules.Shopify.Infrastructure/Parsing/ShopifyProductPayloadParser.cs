using System.Text.Json;
using Modules.Shopify.Application;

namespace Modules.Shopify.Infrastructure.Parsing;

/// <summary>Plan D1: del cuerpo REST de <c>products/create|update|delete</c> sólo se leen <c>id</c> y <c>updated_at</c>; el producto
/// entero se relee por GraphQL. Un cuerpo que no es JSON, o sin id, es <see cref="ShopifyPayloadException"/> (fallo permanente).</summary>
internal sealed class ShopifyProductPayloadParser : IShopifyProductPayloadParser
{
    public ShopifyProductWebhookRef ParseRef(string payload)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException exception)
        {
            throw new ShopifyPayloadException("The Shopify product payload is not valid JSON.", exception);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new ShopifyPayloadException(string.Empty, $"The Shopify product payload must be a JSON object, not {root.ValueKind}.");
            }

            var id = ShopifyJson.OptionalId(root, "id", "id", "Product") ?? throw ShopifyJson.Missing("id");
            return new ShopifyProductWebhookRef(id, ShopifyJson.OptionalDate(root, "updated_at", "updated_at"));
        }
    }
}
