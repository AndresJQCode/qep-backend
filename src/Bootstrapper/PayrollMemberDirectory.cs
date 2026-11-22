using Microsoft.EntityFrameworkCore;
using Modules.Payroll.Application;
using Modules.Tenancy.Domain;
using Modules.Tenancy.Infrastructure.Persistence;

namespace Bootstrapper;

/// <summary>
/// El nombre de quien actúa en el historial del período: la membresía del tenant de un usuario (<c>Membership.UserId</c>). Calco de
/// <see cref="HrMemberDirectory.FindBySubjectAsync"/>. Sin caché; toda lectura va acotada al tenant.
/// </summary>
internal sealed class PayrollMemberDirectory(TenancyDbContext tenancy) : IPayrollMemberDirectory
{
    public async Task<string?> FindDisplayNameAsync(Guid tenantId, Guid subjectId, CancellationToken cancellationToken)
    {
        var tenant = new TenantId(tenantId);
        var membership = await tenancy.Memberships.AsNoTracking()
            .Where(candidate => candidate.TenantId == tenant && candidate.UserId == subjectId)
            .OrderBy(candidate => candidate.State)
            .FirstOrDefaultAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(membership?.DisplayName) ? null : membership.DisplayName;
    }
}
