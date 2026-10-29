using Microsoft.EntityFrameworkCore;
using Modules.Billing.Application;
using Modules.Billing.Domain;

namespace Modules.Billing.Infrastructure.Persistence;

internal sealed class BillingSettingsRepository(BillingDbContext dbContext) : IBillingSettingsRepository
{
    public async Task<BillingSettings> GetOrDefaultAsync(
        Guid tenantId, DateTimeOffset now, CancellationToken cancellationToken) =>
        await dbContext.Settings.FirstOrDefaultAsync(settings => settings.TenantId == tenantId, cancellationToken)
        ?? BillingSettings.Create(tenantId, now);

    public void Upsert(BillingSettings settings)
    {
        // Una fila traída de la base ya está rastreada y su cambio se guarda solo; Add sólo
        // corresponde a los ajustes nuevos que devolvió GetOrDefaultAsync sin fila.
        if (dbContext.Entry(settings).State == EntityState.Detached)
        {
            dbContext.Settings.Add(settings);
        }
    }
}
