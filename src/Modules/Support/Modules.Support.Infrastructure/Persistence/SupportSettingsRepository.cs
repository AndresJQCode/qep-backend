using Microsoft.EntityFrameworkCore;
using Modules.Support.Application;
using Modules.Support.Domain;

namespace Modules.Support.Infrastructure.Persistence;

internal sealed class SupportSettingsRepository(SupportDbContext dbContext) : ISupportSettingsRepository
{
    public async Task<SupportTenantSettings> GetAsync(Guid tenantId, CancellationToken cancellationToken) =>
        await dbContext.TenantSettings.SingleOrDefaultAsync(settings => settings.TenantId == tenantId, cancellationToken)
        ?? throw new InvalidOperationException(
            $"The support settings of tenant '{tenantId}' do not exist; call ISupportProvisioner.EnsureAsync first.");
}
