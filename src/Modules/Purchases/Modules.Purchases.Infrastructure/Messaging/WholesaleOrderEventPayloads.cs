using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Modules.Purchases.Application;

namespace Modules.Purchases.Infrastructure.Messaging;

/// <summary>
/// Mayorista fase 3: el payload es contrato de Quotations (<c>QuotationLifecycleEventPublisher</c>) y se lee por nombre
/// (camelCase), como <c>QuotationEventPayloads</c> de Pipeline. Un payload roto es determinista: reintentarlo no lo arregla, así
/// que se lee como inválido y el procesador lo marca sin efecto. <c>sourcePurchaseOrderLineId</c> nulo es una línea que el
/// mayorista agregó.
/// </summary>
internal static class WholesaleOrderEventPayloads
{
    public static bool TryReadApproved(string json, [NotNullWhen(true)] out WholesaleApproval? approval)
    {
        approval = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var currency = root.GetProperty("currency").GetString();
            var linesElement = root.GetProperty("lines");
            if (string.IsNullOrWhiteSpace(currency) || linesElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var lines = new List<WholesaleApprovedLine>();
            foreach (var line in linesElement.EnumerateArray())
            {
                lines.Add(new WholesaleApprovedLine(
                    OptionalGuid(line, "sourcePurchaseOrderLineId"),
                    line.GetProperty("variantId").GetGuid(),
                    line.GetProperty("code").GetString() ?? string.Empty,
                    line.GetProperty("name").GetString() ?? string.Empty,
                    line.GetProperty("quantity").GetDecimal(),
                    line.GetProperty("unitPrice").GetDecimal(),
                    line.GetProperty("taxPercentage").GetDecimal()));
            }

            approval = new WholesaleApproval(
                root.GetProperty("tenantId").GetGuid(),
                root.GetProperty("orderId").GetGuid(),
                root.GetProperty("buyerTenantId").GetGuid(),
                root.GetProperty("purchaseOrderId").GetGuid(),
                lines,
                currency);
            return true;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    public static bool TryReadCancelled(string json, [NotNullWhen(true)] out WholesaleCancellation? cancellation)
    {
        cancellation = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            cancellation = new WholesaleCancellation(
                root.GetProperty("tenantId").GetGuid(),
                root.GetProperty("orderId").GetGuid(),
                root.GetProperty("buyerTenantId").GetGuid(),
                root.GetProperty("purchaseOrderId").GetGuid());
            return true;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    private static Guid? OptionalGuid(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetGuid() : null;
}
