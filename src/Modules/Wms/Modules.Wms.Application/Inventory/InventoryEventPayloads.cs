using System.Globalization;
using System.Text.Json;
using BuildingBlocks.Domain.Catalog;
using Modules.Wms.Domain.Inventory;

namespace Modules.Wms.Application;

/// <summary>Un mensaje del outbox ya reclamado por el consumidor <c>wms.inventory-events</c>.</summary>
public sealed record InventoryMessage(Guid MessageId, string EventName, string PayloadJson, DateTimeOffset OccurredAt, int Attempts);

/// <summary>
/// Aplica un evento del inventario básico dentro de la transacción de la unidad de trabajo que abrió el consumidor: no confirma
/// nada. Una excepción deshace todo y el mensaje se reintenta.
/// </summary>
public interface IInventoryEventApplier
{
    Task ApplyAsync(InventoryMessage message, CancellationToken cancellationToken);
}

/// <summary>Una línea del documento. <see cref="VariantId"/> es nulo sólo en una orden de compra vinculada al mayorista que todavía no
/// enlazó su variante; <see cref="Kind"/> sólo viaja en los pedidos (el de POS y compras lo resuelve el catálogo).</summary>
public sealed record InventoryEventLine(Guid LineId, Guid? VariantId, decimal Quantity, ProductKind? Kind);

/// <summary>Lo que el consumidor lee de un evento de §5.1.</summary>
public sealed record ParsedInventoryEvent(
    InventoryEventKind Kind,
    string DocumentType,
    Guid TenantId,
    Guid DocumentId,
    long Version,
    DateTimeOffset OccurredAt,
    string? Origin,
    bool IsPrepared,
    bool FromPlatform,
    bool AwaitingWholesaleApproval,
    IReadOnlyList<InventoryEventLine> Lines)
{
    public InventoryEvent ToEvent() => new(Kind, Version, Origin, IsPrepared, FromPlatform, AwaitingWholesaleApproval);
}

/// <summary>
/// Los once eventos del contrato de §5.1, leídos por nombre de campo (camelCase). <c>wholesale-cancelled</c> no está: el
/// <c>cancelled</c> ya libera, y consumir los dos liberaría dos veces.
/// </summary>
public static class InventoryEventPayloads
{
    private static readonly Dictionary<string, (InventoryEventKind Kind, string DocumentType, string IdProperty, bool HasLines)> Events =
        new(StringComparer.Ordinal)
        {
            ["quotations.order.created.v1"] = (InventoryEventKind.OrderCreated, InventoryDocumentTypes.Order, "orderId", true),
            ["quotations.order.wholesale-approved.v1"] = (InventoryEventKind.OrderWholesaleApproved, InventoryDocumentTypes.Order, "orderId", true),
            ["quotations.order.edited.v1"] = (InventoryEventKind.OrderEdited, InventoryDocumentTypes.Order, "orderId", true),
            ["quotations.order.prepared.v1"] = (InventoryEventKind.OrderPrepared, InventoryDocumentTypes.Order, "orderId", true),
            ["quotations.order.unprepared.v1"] = (InventoryEventKind.OrderUnprepared, InventoryDocumentTypes.Order, "orderId", false),
            ["quotations.order.cancelled.v1"] = (InventoryEventKind.OrderCancelled, InventoryDocumentTypes.Order, "orderId", false),
            ["quotations.order.archived.v1"] = (InventoryEventKind.OrderArchived, InventoryDocumentTypes.Order, "orderId", true),
            ["quotations.order.unarchived.v1"] = (InventoryEventKind.OrderUnarchived, InventoryDocumentTypes.Order, "orderId", false),
            ["purchases.order.received.v1"] = (InventoryEventKind.PurchaseReceived, InventoryDocumentTypes.PurchaseOrder, "purchaseOrderId", true),
            ["pos.sale.created.v1"] = (InventoryEventKind.PosCreated, InventoryDocumentTypes.PosSale, "saleId", true),
            ["pos.sale.voided.v1"] = (InventoryEventKind.PosVoided, InventoryDocumentTypes.PosSale, "saleId", false),
        };

    /// <summary>Los nombres de evento que atiende el consumidor.</summary>
    public static IReadOnlyList<string> EventNames { get; } = [.. Events.Keys];

    public static bool TryParse(string eventName, string payloadJson, out ParsedInventoryEvent parsed)
    {
        parsed = null!;
        if (!Events.TryGetValue(eventName, out var descriptor))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryGuid(root, "tenantId", out var tenantId)
                || !TryGuid(root, descriptor.IdProperty, out var documentId)
                || !root.TryGetProperty("version", out var version) || !version.TryGetInt64(out var versionValue)
                || !root.TryGetProperty("occurredAt", out var occurred) || !occurred.TryGetDateTimeOffset(out var occurredAt))
            {
                return false;
            }

            var lines = new List<InventoryEventLine>();
            if (descriptor.HasLines && !TryReadLines(root, lines))
            {
                return false;
            }

            parsed = new ParsedInventoryEvent(
                descriptor.Kind,
                descriptor.DocumentType,
                tenantId,
                documentId,
                versionValue,
                occurredAt,
                root.TryGetProperty("origin", out var origin) && origin.ValueKind == JsonValueKind.String ? origin.GetString() : null,
                Flag(root, "isPrepared"),
                Flag(root, "fromPlatform"),
                Flag(root, "awaitingWholesaleApproval"),
                lines);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadLines(JsonElement root, List<InventoryEventLine> lines)
    {
        if (!root.TryGetProperty("lines", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var line in array.EnumerateArray())
        {
            if (line.ValueKind != JsonValueKind.Object
                || !TryGuid(line, "lineId", out var lineId)
                || !line.TryGetProperty("quantity", out var quantity) || !quantity.TryGetDecimal(out var quantityValue))
            {
                return false;
            }

            Guid? variantId = null;
            if (line.TryGetProperty("variantId", out var variant) && variant.ValueKind != JsonValueKind.Null)
            {
                if (!TryGuid(line, "variantId", out var variantValue))
                {
                    return false;
                }

                variantId = variantValue;
            }

            // Una cantidad que no es positiva no se puede asentar y no es un dato del que valga esperar: no es línea.
            if (quantityValue <= 0m)
            {
                continue;
            }

            ProductKind? kind = line.TryGetProperty("productKind", out var kindElement)
                && kindElement.ValueKind == JsonValueKind.String
                && Enum.TryParse<ProductKind>(kindElement.GetString(), ignoreCase: true, out var kindValue)
                ? kindValue
                : null;
            lines.Add(new InventoryEventLine(lineId, variantId, quantityValue, kind));
        }

        return true;
    }

    private static bool TryGuid(JsonElement element, string name, out Guid value)
    {
        value = Guid.Empty;
        return element.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String
            && Guid.TryParse(property.GetString(), CultureInfo.InvariantCulture, out value);
    }

    private static bool Flag(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.True;
}
