using Microsoft.EntityFrameworkCore;
using Modules.ServiceContracts.Application;
using Modules.ServiceContracts.Domain;

namespace Modules.ServiceContracts.Infrastructure.Persistence;

internal sealed class ServiceContractsSettingsRepository(ServiceContractsDbContext dbContext) : IServiceContractsSettingsRepository
{
    public async Task<ServiceContractsSettings> GetOrDefaultAsync(
        Guid tenantId, DateTimeOffset now, CancellationToken cancellationToken) =>
        await dbContext.Settings.FirstOrDefaultAsync(settings => settings.TenantId == tenantId, cancellationToken)
        ?? ServiceContractsSettings.Create(tenantId, now);

    public void Upsert(ServiceContractsSettings settings)
    {
        // Una fila traída de la base ya está rastreada y su cambio se guarda solo; Add sólo
        // corresponde a los ajustes nuevos que devolvió GetOrDefaultAsync sin fila.
        if (dbContext.Entry(settings).State == EntityState.Detached)
        {
            dbContext.Settings.Add(settings);
        }
    }
}
