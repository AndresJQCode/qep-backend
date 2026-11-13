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
        if (taxRate is null)
        {
            return null;
        }

        // VatPercentage tira con una tasa que retiene: el handler rechaza esa tasa por AddsTax.
        return taxRate.Withholds
            ? new QuotationTaxRateRef(taxRate.Id.Value, taxRate.Name, 0, taxRate.IsActive, AddsTax: false)
            : new QuotationTaxRateRef(taxRate.Id.Value, taxRate.Name, taxRate.VatPercentage, taxRate.IsActive);
    }
}
