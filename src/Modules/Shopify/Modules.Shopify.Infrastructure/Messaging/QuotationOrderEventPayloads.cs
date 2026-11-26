using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Modules.Shopify.Application;

namespace Modules.Shopify.Infrastructure.Messaging;

/// <summary>
/// Spec 2026-11-24 §11.1.1: el payload lo escribe <c>QuotationLifecycleEventPublisher</c> y acá se lee por nombre (camelCase), como
/// <see cref="CitySynonymEventPayloads"/>. Renombrar un campo de un lado compila y deja los mensajes procesados sin efecto: los
/// amarran <c>OrderEditsApiTests</c> (lo que se publica) y <c>ShopifyDeliveryEditPushTests</c> (lo que se lee). Un payload roto es
/// determinista —reintentarlo no lo arregla—, así que se lee como inválido y el consumidor lo marca procesado.
/// </summary>
internal static class QuotationOrderEventPayloads
{
    public static bool TryReadShippingPartyChanged(string json, [NotNullWhen(true)] out ShopifyShippingPartyChangedFact? fact)
    {
        fact = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var source = root.GetProperty("source").GetString();
            if (string.IsNullOrWhiteSpace(source))
            {
                return false;
            }

            fact = new ShopifyShippingPartyChangedFact(
                root.GetProperty("tenantId").GetGuid(),
                root.GetProperty("orderId").GetGuid(),
                source,
                root.GetProperty("occurredAt").GetDateTimeOffset());
            return true;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }
}
