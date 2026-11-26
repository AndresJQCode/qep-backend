using Microsoft.EntityFrameworkCore;
using Modules.Hr.Application;
using Modules.Hr.Domain;

namespace Modules.Hr.Infrastructure.Persistence;

internal sealed class HrMemberPhones(HrDbContext dbContext) : IHrMemberPhones
{
    public async Task<IReadOnlyList<HrMemberPhone>> ListAsync(
        Guid tenantId,
        IReadOnlyCollection<Guid> memberIds,
        CancellationToken cancellationToken)
    {
        if (memberIds.Count == 0)
        {
            return [];
        }

        var ids = memberIds.ToArray();
        var rows = await dbContext.Employees
            .AsNoTracking()
            .Where(employee => employee.TenantId == tenantId
                && employee.Status == EmployeeStatus.Active
                && employee.MemberId != null
                && ids.Contains(employee.MemberId.Value)
                && employee.Phone != null
                && employee.Phone != "")
            .Select(employee => new { MemberId = employee.MemberId!.Value, Phone = employee.Phone! })
            .ToListAsync(cancellationToken);

        return rows.Select(row => new HrMemberPhone(row.MemberId, row.Phone)).ToList();
    }
}
