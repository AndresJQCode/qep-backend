using Modules.Hr.Domain;

namespace Modules.Hr.Application.BirthdayNotices;

/// <summary>The birthday notice setting of a tenant (at most one row). Every method carries the tenant.</summary>
public interface IBirthdayNoticeSettingRepository
{
    Task<BirthdayNoticeSetting?> FindAsync(Guid tenantId, CancellationToken cancellationToken);

    void Add(BirthdayNoticeSetting setting);
}
