using Microsoft.EntityFrameworkCore;
using Modules.Quotations.Application;

namespace Modules.Quotations.Infrastructure.Persistence;

internal sealed class QuotationSettingsStore(QuotationsDbContext dbContext) : IQuotationSettingsStore
{
    public async Task<QuotationSettings> GetAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var row = await dbContext.TenantQuotationSettings
            .AsNoTracking()
            .SingleOrDefaultAsync(settings => settings.TenantId == tenantId, cancellationToken);
        // No row: a tenant registered after AddQuotationSettings ran (the migration only seeds the
        // tenants that already existed) gets the same values the seed wrote.
        if (row is null)
        {
            return QuotationSettings.Default;
        }

        var totals = await dbContext.TenantMinimumTotals
            .AsNoTracking()
            .Where(total => total.TenantId == tenantId)
            .ToListAsync(cancellationToken);

        // Trim: character(3) pads, and the codes are compared ordinally against Quotation.Currency.
        return new QuotationSettings(
            row.MinimumUnits,
            totals.ToDictionary(total => total.Currency.Trim(), total => total.Amount, StringComparer.Ordinal));
    }

    // Diffed and not delete-all-then-insert: a removed and an added row with the same
    // (tenant_id, currency) key cannot be tracked in one SaveChanges.
    public async Task SaveAsync(Guid tenantId, QuotationSettings settings, CancellationToken cancellationToken)
    {
        var row = await dbContext.TenantQuotationSettings
            .SingleOrDefaultAsync(existing => existing.TenantId == tenantId, cancellationToken);
        if (row is null)
        {
            dbContext.TenantQuotationSettings.Add(
                new TenantQuotationSettingsRow { TenantId = tenantId, MinimumUnits = settings.MinimumUnits });
        }
        else
        {
            row.MinimumUnits = settings.MinimumUnits;
        }

        var stored = await dbContext.TenantMinimumTotals
            .Where(total => total.TenantId == tenantId)
            .ToListAsync(cancellationToken);
        foreach (var total in stored)
        {
            if (settings.MinimumTotals.TryGetValue(total.Currency.Trim(), out var amount))
            {
                total.Amount = amount;
            }
            else
            {
                dbContext.TenantMinimumTotals.Remove(total);
            }
        }

        foreach (var (currency, amount) in settings.MinimumTotals)
        {
            if (!stored.Any(total => total.Currency.Trim() == currency))
            {
                dbContext.TenantMinimumTotals.Add(
                    new TenantMinimumTotalRow { TenantId = tenantId, Currency = currency, Amount = amount });
            }
        }
    }
}
