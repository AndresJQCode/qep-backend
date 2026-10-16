namespace Modules.Quotations.Application;

/// <summary>Lee una tasa de impuesto de Catalog sin referenciarlo (spec 2026-10-17 §4.2): el
/// adaptador vive en Bootstrapper, mismo patrón que <see cref="IQuotationProductPricingLookup"/>.
/// <c>null</c> si no existe en el tenant.</summary>
public interface IQuotationTaxRateLookup
{
    Task<QuotationTaxRateRef?> FindAsync(Guid tenantId, Guid taxRateId, CancellationToken cancellationToken);
}

public sealed record QuotationTaxRateRef(Guid Id, string Name, int Percentage, bool IsActive);
