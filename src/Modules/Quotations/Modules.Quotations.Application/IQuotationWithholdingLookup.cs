using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// Lee de Catalog y de Customers las tasas de retención que un documento congela (spec impuestos por tipo §7.5), sin
/// referenciar a ninguno de los dos: el adaptador vive en Bootstrapper, mismo patrón que
/// <see cref="IQuotationTaxRateLookup"/>. Cada tasa sale ya como foto (<see cref="WithholdingRate"/>), con el valor
/// de la unidad tributaria resuelto para <c>documentDate</c>.
/// </summary>
public interface IQuotationWithholdingLookup
{
    /// <summary>Las tasas asignadas al cliente, en su orden. <paramref name="country"/> null = todas, de cualquier
    /// país (la cotización, D16); con país, sólo las de ése (los pedidos, Tarea 14). Un cliente que no existe no
    /// tiene tasas.</summary>
    Task<IReadOnlyList<WithholdingRate>> ForCustomerAsync(
        Guid tenantId, Guid customerId, string? country, DateOnly documentDate, CancellationToken cancellationToken);

    /// <summary>Las tasas del tenant con esos ids, para validar un selector. Un id que no está en el resultado no
    /// existe en el tenant.</summary>
    Task<IReadOnlyDictionary<Guid, WithholdingRateCandidate>> ForRatesAsync(
        Guid tenantId, IReadOnlyCollection<Guid> taxRateIds, DateOnly documentDate, CancellationToken cancellationToken);
}

/// <summary>Una tasa del catálogo con lo que <see cref="WithholdingSelection"/> necesita para aceptarla en un
/// selector: si está activa y si retiene.</summary>
public sealed record WithholdingRateCandidate(WithholdingRate Rate, bool IsActive, bool Withholds);
