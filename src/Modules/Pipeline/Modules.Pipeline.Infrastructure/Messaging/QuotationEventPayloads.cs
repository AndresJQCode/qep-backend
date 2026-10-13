using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Modules.Pipeline.Application;

namespace Modules.Pipeline.Infrastructure.Messaging;

/// <summary>Spec 2026-10-13 §4.5: el payload es contrato de Quotations y se lee por nombre (camelCase), como
/// <c>PaymentProofMoveProcessor</c> lee el de los comprobantes. Un payload roto es determinista: reintentarlo no lo arregla, así
/// que se lee como inválido y el worker lo marca procesado. Un total de 0 es un monto válido.</summary>
internal static class QuotationEventPayloads
{
    public static bool TryReadQuotation(string json, [NotNullWhen(true)] out QuotationFact? fact)
    {
        fact = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var number = root.GetProperty("quotationNumber").GetString();
            var currency = root.GetProperty("currency").GetString();
            if (string.IsNullOrWhiteSpace(number) || string.IsNullOrWhiteSpace(currency))
            {
                return false;
            }

            Guid? orderId = root.TryGetProperty("orderId", out var order) && order.ValueKind == JsonValueKind.String ? order.GetGuid() : null;
            fact = new QuotationFact(
                root.GetProperty("tenantId").GetGuid(),
                root.GetProperty("quotationId").GetGuid(),
                number,
                root.GetProperty("clientId").GetGuid(),
                root.GetProperty("advisorMemberId").GetGuid(),
                root.GetProperty("total").GetDecimal(),
                currency,
                orderId);
            return true;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    public static bool TryReadOrderCancelled(string json, [NotNullWhen(true)] out OrderCancelledFact? fact)
    {
        fact = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            fact = new OrderCancelledFact(
                root.GetProperty("tenantId").GetGuid(),
                root.GetProperty("orderId").GetGuid(),
                root.GetProperty("quotationId").GetGuid());
            return true;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }
}
