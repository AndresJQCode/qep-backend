using System.Globalization;
using System.Text.Json;
using Modules.Geography.Application;
using Modules.Geography.Domain;

namespace Modules.Geography.Infrastructure.Persistence;

/// <summary>
/// Copia del patrón de <c>QuotationLifecycleEventPublisher</c>: cada evento es una <see cref="GeographyOutboxMessage"/> del mismo
/// <see cref="GeographyDbContext"/>, así que commitea o revierte con el sinónimo. Los nombres en camelCase son el contrato que
/// lee <c>CitySynonymEventPayloads</c> (Shopify); lo amarran <c>GeographyEventPublisherTests</c> y
/// <c>CitySynonymEventPayloadsTests</c>, con el mismo literal.
/// </summary>
internal sealed class GeographyEventPublisher(GeographyDbContext dbContext) : IGeographyEventPublisher
{
    public void PublishSynonymResolved(
        CitySynonym synonym, IReadOnlyList<UnresolvedCityOccurrence> occurrences, Guid operatorTenantId, DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(synonym);
        ArgumentNullException.ThrowIfNull(occurrences);
        Add(CitySynonymEventNames.Resolved, synonym, new ResolvedPayload(
            synonym.Id.Value,
            synonym.DepartmentId.Value,
            synonym.CityId.Value,
            synonym.NormalizedText,
            synonym.CreatedBy,
            operatorTenantId,
            occurredAt,
            occurrences
                .Select(occurrence => new OccurrencePayload(
                    occurrence.Id,
                    occurrence.TenantId,
                    occurrence.Source,
                    occurrence.SourceRef,
                    occurrence.OrderId,
                    occurrence.CustomerId,
                    occurrence.PartyCityOnly,
                    occurrence.CustomerCityOnly))
                .ToList()), occurredAt);
    }

    public void PublishSynonymCorrected(CitySynonym synonym, CityId previousCityId, Guid operatorTenantId, DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(synonym);
        Add(CitySynonymEventNames.Corrected, synonym, new CorrectedPayload(
            synonym.Id.Value,
            synonym.DepartmentId.Value,
            previousCityId.Value,
            synonym.CityId.Value,
            synonym.UpdatedBy,
            operatorTenantId,
            occurredAt), occurredAt);
    }

    private void Add<TPayload>(string eventName, CitySynonym synonym, TPayload payload, DateTimeOffset occurredAt) =>
        dbContext.OutboxMessages.Add(new GeographyOutboxMessage
        {
            Id = Guid.CreateVersion7(),
            EventName = eventName,
            PayloadJson = JsonSerializer.Serialize(payload),
            CorrelationId = synonym.Id.Value.ToString("D", CultureInfo.InvariantCulture),
            OccurredAt = occurredAt,
        });

    // Los nombres en camelCase son el contrato: System.Text.Json serializa los records con el nombre del parámetro tal cual.
    private sealed record ResolvedPayload(
        Guid synonymId, Guid departmentId, Guid cityId, string normalizedText, Guid operatorSubjectId, Guid operatorTenantId,
        DateTimeOffset occurredAt, IReadOnlyList<OccurrencePayload> occurrences);

    private sealed record OccurrencePayload(
        Guid occurrenceId, Guid tenantId, string source, Guid sourceRef, Guid orderId, Guid? customerId, bool partyCityOnly,
        bool customerCityOnly);

    private sealed record CorrectedPayload(
        Guid synonymId, Guid departmentId, Guid previousCityId, Guid cityId, Guid operatorSubjectId, Guid operatorTenantId,
        DateTimeOffset occurredAt);
}
