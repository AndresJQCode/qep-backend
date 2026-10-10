using Microsoft.EntityFrameworkCore;
using Modules.Catalog.Application;
using Modules.Tenancy.Application;

namespace Modules.Catalog.Infrastructure.Persistence;

internal sealed class CurrenciesInUse(CatalogDbContext dbContext) : ICurrenciesInUse
{
    public async Task<IReadOnlyList<string>> GetAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        // DISTINCT over product_prices joined to the tenant's products, in SQL. Inactive products
        // count: their prices are still the tenant's data.
        var codes = await dbContext.Products
            .AsNoTracking()
            .Where(product => product.TenantId == tenantId)
            .SelectMany(product => product.Prices)
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
