using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Modules.Shopify.Application;

namespace Modules.Shopify.Infrastructure.Messaging;

/// <summary>
/// Spec 2026-10-26, «Contrato de eventos»: el payload lo escribe <c>GeographyEventPublisher</c> y acá se lee por nombre (camelCase),
/// como <c>QuotationEventPayloads</c> en Pipeline. Renombrar un campo de un lado compila y deja los mensajes procesados sin efecto:
/// los amarran <c>CitySynonymEventPayloadsTests</c> y <c>GeographyEventPublisherTests</c>, con el mismo literal. Un payload roto es
/// determinista —reintentarlo no lo arregla—, así que se lee como inválido y el consumidor lo marca procesado.
/// </summary>
internal static class CitySynonymEventPayloads
{
    public static bool TryReadResolved(string json, [NotNullWhen(true)] out CitySynonymResolvedFact? fact)
    {
        fact = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var normalizedText = root.GetProperty("normalizedText").GetString();
            if (string.IsNullOrWhiteSpace(normalizedText))
            {
                return false;
            }

            var occurrences = new List<CitySynonymOccurrenceFact>();
            foreach (var occurrence in root.GetProperty("occurrences").EnumerateArray())
            {
                var source = occurrence.GetProperty("source").GetString();
                if (string.IsNullOrWhiteSpace(source))
                {
                    return false;
                }

                var customer = occurrence.GetProperty("customerId");
                occurrences.Add(new CitySynonymOccurrenceFact(
                    occurrence.GetProperty("occurrenceId").GetGuid(),
                    occurrence.GetProperty("tenantId").GetGuid(),
                    source,
                    occurrence.GetProperty("sourceRef").GetGuid(),
                    occurrence.GetProperty("orderId").GetGuid(),
                    customer.ValueKind == JsonValueKind.Null ? null : customer.GetGuid(),
                    occurrence.GetProperty("partyCityOnly").GetBoolean(),
                    occurrence.GetProperty("customerCityOnly").GetBoolean()));
            }

            fact = new CitySynonymResolvedFact(
                root.GetProperty("synonymId").GetGuid(),
                root.GetProperty("departmentId").GetGuid(),
                root.GetProperty("cityId").GetGuid(),
                normalizedText,
                root.GetProperty("operatorSubjectId").GetGuid(),
                root.GetProperty("operatorTenantId").GetGuid(),
                occurrences);
            return true;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    public static bool TryReadCorrected(string json, [NotNullWhen(true)] out CitySynonymCorrectedFact? fact)
    {
        fact = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            fact = new CitySynonymCorrectedFact(
                root.GetProperty("synonymId").GetGuid(),
                root.GetProperty("departmentId").GetGuid(),
                root.GetProperty("previousCityId").GetGuid(),
                root.GetProperty("cityId").GetGuid(),
                root.GetProperty("operatorSubjectId").GetGuid(),
                root.GetProperty("operatorTenantId").GetGuid());
            return true;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }
}
