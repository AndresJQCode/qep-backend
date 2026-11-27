using Microsoft.EntityFrameworkCore;
using Modules.Hr.Application.BirthdayNotices;
using Modules.Hr.Domain;

namespace Modules.Hr.Infrastructure.Persistence;

internal sealed class BirthdayNoticeSettingRepository(HrDbContext dbContext) : IBirthdayNoticeSettingRepository
{
    public Task<BirthdayNoticeSetting?> FindAsync(Guid tenantId, CancellationToken cancellationToken) =>
        dbContext.BirthdayNoticeSettings.SingleOrDefaultAsync(setting => setting.TenantId == tenantId, cancellationToken);

    public void Add(BirthdayNoticeSetting setting) => dbContext.BirthdayNoticeSettings.Add(setting);
}
