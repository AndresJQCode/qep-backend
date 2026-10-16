using Modules.Catalog.Application;
using Modules.Catalog.Domain;
using Modules.Quotations.Application;

namespace Bootstrapper;

/// <summary>
/// Adapta el repositorio de tasas de <c>Catalog</c> al puerto que <c>quotations</c> declara para el
/// IVA del envío (spec 2026-10-17 §4.2). Mismo aislamiento que
/// <see cref="QuotationProductPricingLookup"/>: no decide nada, sólo traduce. Si la tasa sirve
/// (activa) lo decide el handler.
/// </summary>
internal sealed class QuotationTaxRateLookup(ITaxRateRepository repository) : IQuotationTaxRateLookup
{
    public async Task<QuotationTaxRateRef?> FindAsync(
        Guid tenantId, Guid taxRateId, CancellationToken cancellationToken)
    {
        var taxRate = await repository.FindAsync(tenantId, new TaxRateId(taxRateId), cancellationToken);
        return taxRate is null
            ? null
            : new QuotationTaxRateRef(taxRate.Id.Value, taxRate.Name, taxRate.Percentage, taxRate.IsActive);
    }
}
