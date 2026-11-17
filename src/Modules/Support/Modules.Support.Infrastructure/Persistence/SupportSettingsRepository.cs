using Microsoft.EntityFrameworkCore;
using Modules.Support.Application;
using Modules.Support.Domain;

namespace Modules.Support.Infrastructure.Persistence;

internal sealed class SupportSettingsRepository(SupportDbContext dbContext) : ISupportSettingsRepository
{
    public async Task<SupportTenantSettings> GetAsync(Guid tenantId, CancellationToken cancellationToken) =>
        await dbContext.TenantSettings.SingleOrDefaultAsync(settings => settings.TenantId == tenantId, cancellationToken)
        ?? throw MissingRow(tenantId);

    public Task<SupportTenantSettings?> FindAsync(Guid tenantId, CancellationToken cancellationToken) =>
        dbContext.TenantSettings.AsNoTracking().SingleOrDefaultAsync(settings => settings.TenantId == tenantId, cancellationToken);

    public async Task LockAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        // Sin transacción el candado se soltaría al terminar la sentencia y no serializaría nada.
        if (dbContext.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "Locking the support settings needs an open transaction (ISupportUnitOfWork.BeginTransactionAsync).");
        }

        var locked = await dbContext.Database
            .SqlQuery<Guid>($"""
                SELECT tenant_id AS "Value" FROM support.tenant_settings WHERE tenant_id = {tenantId} FOR UPDATE
                """)
            .ToListAsync(cancellationToken);
        if (locked.Count != 1)
        {
            throw MissingRow(tenantId);
        }
    }

    private static InvalidOperationException MissingRow(Guid tenantId) =>
        new($"The support settings of tenant '{tenantId}' do not exist; call ISupportProvisioner.EnsureAsync first.");
}
