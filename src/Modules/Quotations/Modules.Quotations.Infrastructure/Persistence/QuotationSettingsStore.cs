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

    // Serialized per tenant: the parent row is created if missing (ON CONFLICT waits for, then
    // yields to, a concurrent first save) and locked FOR UPDATE until the caller's transaction
    // ends. Only then are the children read, so the diff runs against the last committed document
    // and the result is exactly this body, never a merge with another save's.
    //
    // Diffed and not delete-all-then-insert: a removed and an added row with the same
    // (tenant_id, currency) key cannot be tracked in one SaveChanges.
    public async Task SaveAsync(Guid tenantId, QuotationSettings settings, CancellationToken cancellationToken)
    {
        var row = await LockRowAsync(tenantId, settings.MinimumUnits, nameof(SaveAsync), cancellationToken);
        // Only the minimum units: the freight tax rate lives on the same row but is not part of this
        // document (P2), so it stays as it was.
        row.MinimumUnits = settings.MinimumUnits;

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

    public async Task<Guid?> GetFreightTaxRateIdAsync(Guid tenantId, CancellationToken cancellationToken) =>
        await dbContext.TenantQuotationSettings
            .AsNoTracking()
            .Where(settings => settings.TenantId == tenantId)
            .Select(settings => settings.FreightTaxRateId)
            .SingleOrDefaultAsync(cancellationToken);

    // Same lock as SaveAsync: a first save of either document creates the row with the default
    // minimum units (P2), and the other one finds it.
    public async Task SetFreightTaxRateIdAsync(Guid tenantId, Guid? taxRateId, CancellationToken cancellationToken)
    {
        var row = await LockRowAsync(
            tenantId, QuotationSettings.Default.MinimumUnits, nameof(SetFreightTaxRateIdAsync), cancellationToken);
        row.FreightTaxRateId = taxRateId;
    }

    // The parent row is created if missing (ON CONFLICT waits for, then yields to, a concurrent
    // first save) and locked FOR UPDATE until the caller's transaction ends.
    private async Task<TenantQuotationSettingsRow> LockRowAsync(
        Guid tenantId, int minimumUnitsIfMissing, string caller, CancellationToken cancellationToken)
    {
        // Without a transaction the lock would be released as soon as the SELECT ends.
        if (dbContext.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                $"QuotationSettingsStore.{caller} must run inside IQuotationsUnitOfWork.BeginTransactionAsync.");
        }

        await dbContext.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO quotations.tenant_quotation_settings (tenant_id, minimum_units)
            VALUES ({tenantId}, {minimumUnitsIfMissing})
            ON CONFLICT (tenant_id) DO NOTHING
            """,
            cancellationToken);

        // Not composed (no Single/First on top): EF would wrap FOR UPDATE in a subquery. Every
        // mapped column goes in the SELECT, or EF fails to materialize the row.
        return (await dbContext.TenantQuotationSettings
            .FromSql(
                $"""
                SELECT tenant_id, minimum_units, freight_tax_rate_id FROM quotations.tenant_quotation_settings
                WHERE tenant_id = {tenantId}
                FOR UPDATE
                """)
            .ToListAsync(cancellationToken))
            .Single();
    }
}
