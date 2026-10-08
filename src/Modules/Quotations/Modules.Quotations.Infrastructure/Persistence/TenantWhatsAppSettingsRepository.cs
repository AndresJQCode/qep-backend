using Microsoft.EntityFrameworkCore;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.Infrastructure.Persistence;

internal sealed class TenantWhatsAppSettingsRepository(QuotationsDbContext dbContext)
    : ITenantWhatsAppSettingsRepository
{
    public Task<TenantWhatsAppSettings?> FindAsync(Guid tenantId, CancellationToken cancellationToken) =>
        dbContext.WhatsAppSettings.SingleOrDefaultAsync(
            settings => settings.TenantId == tenantId, cancellationToken);

    public Task<TenantWhatsAppSettings?> FindReadOnlyAsync(Guid tenantId, CancellationToken cancellationToken) =>
        dbContext.WhatsAppSettings.AsNoTracking().SingleOrDefaultAsync(
            settings => settings.TenantId == tenantId, cancellationToken);

    public void Add(TenantWhatsAppSettings settings) => dbContext.WhatsAppSettings.Add(settings);
}
