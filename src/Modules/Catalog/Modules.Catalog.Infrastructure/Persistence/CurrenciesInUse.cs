using Microsoft.EntityFrameworkCore;
using Modules.Catalog.Application;
using Modules.Tenancy.Application;

namespace Modules.Catalog.Infrastructure.Persistence;

internal sealed class CurrenciesInUse(CatalogDbContext dbContext) : ICurrenciesInUse
{
    public async Task<IReadOnlyList<string>> GetAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        // DISTINCT over product_prices joined to the tenant's variants, in SQL. Inactive products
        // and variants count: their prices are still the tenant's data.
        var codes = await dbContext.ProductVariants
            .AsNoTracking()
            .Where(variant => variant.TenantId == tenantId)
            .SelectMany(variant => variant.Prices)
            .Select(price => price.Currency)
            .Distinct()
            .ToListAsync(cancellationToken);

        return codes
            .Select(code => code.Trim())
            .OrderBy(Currencies.OrderOf)
            .ThenBy(code => code, StringComparer.Ordinal)
            .ToArray();
    }
}
