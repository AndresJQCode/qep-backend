using Modules.Geography.Domain;

namespace Modules.Geography.Application;

/// <summary>Los eventos de sinónimos de ciudad («Contrato de eventos» del plan). Los lee Shopify por nombre
/// (<c>CitySynonymEventsProcessor</c>, que tiene su propia copia de estos dos textos).</summary>
public static class CitySynonymEventNames
{
    public const string Resolved = "geography.city_synonym.resolved.v1";
    public const string Corrected = "geography.city_synonym.corrected.v1";
}

/// <summary>
/// Escribe los eventos de sinónimos en el outbox de plataforma dentro de la transacción de Geography: salen con el
/// <c>SaveChanges</c> del cambio o no salen. La correlación es el id del sinónimo, y los nombres de campo son contrato con
/// <c>CitySynonymEventPayloads</c> (Shopify).
/// </summary>
public interface IGeographyEventPublisher
{
    /// <summary>Un texto confirmado como sinónimo, con todas las ocurrencias de la fila: Shopify siembra un trabajo de corrección
    /// por pedido o cliente. El operador es <see cref="CitySynonym.CreatedBy"/>.</summary>
    void PublishSynonymResolved(
        CitySynonym synonym, IReadOnlyList<UnresolvedCityOccurrence> occurrences, Guid operatorTenantId, DateTimeOffset occurredAt);

    /// <summary>Un sinónimo que pasó a otra ciudad (SC-12). El operador es <see cref="CitySynonym.UpdatedBy"/>.</summary>
    void PublishSynonymCorrected(CitySynonym synonym, CityId previousCityId, Guid operatorTenantId, DateTimeOffset occurredAt);
}
