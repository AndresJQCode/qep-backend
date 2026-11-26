using Microsoft.EntityFrameworkCore;
using Modules.Payroll.Application;
using Modules.Payroll.Domain;

namespace Modules.Payroll.Infrastructure.Persistence;

internal sealed class VacationRepository(PayrollDbContext dbContext) : IVacationRepository
{
    public Task<Vacation?> FindAsync(Guid tenantId, VacationId id, CancellationToken cancellationToken) =>
        dbContext.Vacations.SingleOrDefaultAsync(
            vacation => vacation.TenantId == tenantId && vacation.Id == id, cancellationToken);

    public async Task<IReadOnlyList<Vacation>> ListByEmployeeAsync(
        Guid tenantId, Guid companyId, Guid employeeId, CancellationToken cancellationToken) =>
        await dbContext.Vacations
            .AsNoTracking()
            .Where(vacation => vacation.TenantId == tenantId && vacation.CompanyId == companyId && vacation.EmployeeId == employeeId)
            .OrderByDescending(vacation => vacation.StartDate)
            .ThenBy(vacation => vacation.Id)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Vacation>> ListOverlappingAsync(
        Guid tenantId, Guid companyId, DateOnly firstDay, DateOnly lastDay, CancellationToken cancellationToken) =>
        await dbContext.Vacations
            .AsNoTracking()
            .Where(vacation => vacation.TenantId == tenantId
                && vacation.CompanyId == companyId
                && vacation.StartDate <= lastDay
                && vacation.EndDate >= firstDay)
            .ToListAsync(cancellationToken);

    public void Add(Vacation vacation) => dbContext.Vacations.Add(vacation);

    public void Remove(Vacation vacation) => dbContext.Vacations.Remove(vacation);
}
