namespace Modules.Quotations.Application;

/// <summary>Lee una tasa de impuesto de Catalog sin referenciarlo (spec 2026-10-17 §4.2): el
/// adaptador vive en Bootstrapper, mismo patrón que <see cref="IQuotationProductPricingLookup"/>.
/// <c>null</c> si no existe en el tenant.</summary>
public interface IQuotationTaxRateLookup
{
    Task<QuotationTaxRateRef?> FindAsync(Guid tenantId, Guid taxRateId, CancellationToken cancellationToken);
}

/// <param name="AddsTax"><c>false</c> si la tasa retiene: entonces <c>Percentage</c> es 0, porque un
/// porcentaje de retención no se lee como IVA.</param>
public sealed record QuotationTaxRateRef(Guid Id, string Name, int Percentage, bool IsActive, bool AddsTax = true);
