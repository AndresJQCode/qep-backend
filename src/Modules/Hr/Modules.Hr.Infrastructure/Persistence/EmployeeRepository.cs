using Microsoft.EntityFrameworkCore;
using Modules.Hr.Application;
using Modules.Hr.Domain;

namespace Modules.Hr.Infrastructure.Persistence;

internal sealed class EmployeeRepository(HrDbContext dbContext) : IEmployeeRepository
{
    public Task<Employee?> FindAsync(Guid tenantId, EmployeeId id, CancellationToken cancellationToken) =>
        WithHistories(dbContext.Employees)
            .AsSplitQuery()
            .SingleOrDefaultAsync(employee => employee.TenantId == tenantId && employee.Id == id, cancellationToken);

    public void Add(Employee employee) => dbContext.Employees.Add(employee);

    public void AddRange(IEnumerable<Employee> employees) => dbContext.Employees.AddRange(employees);

    public void Remove(Employee employee) => dbContext.Employees.Remove(employee);

    public async Task<EmployeeListPage> ListAsync(
        Guid tenantId,
        string? search,
        EmployeeStatus? status,
        bool expiringSoon,
        Guid? companyId,
        DateOnly today,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var searched = dbContext.Employees.AsNoTracking().Where(employee => employee.TenantId == tenantId);
        var pattern = LikeSearch.ContainsPattern(search);
        if (pattern is not null)
        {
            searched = searched.Where(employee =>
                EF.Functions.ILike(employee.FirstName + " " + employee.LastName, pattern, LikeSearch.EscapeCharacter)
                || EF.Functions.ILike(employee.DocumentNumber, pattern, LikeSearch.EscapeCharacter));
        }

        // Los contadores respetan la búsqueda y nada más: la pantalla los pinta como pestañas y no se mueven al filtrar.
        var byStatus = await searched
            .GroupBy(employee => employee.Status)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);
        int CountOf(EmployeeStatus value) => byStatus.Where(row => row.Status == value).Sum(row => row.Count);

        // Fijado fuera de la consulta: el servidor no calcula «hoy», es el del tenant.
        var noticeLimit = today.AddDays(FixedTermLimits.NoticeDays);
        var expiring = searched.Where(employee => employee.Contracts.Any(contract =>
            contract.Status == ContractStatus.Active
            && contract.Duration == ContractDuration.FixedTerm
            && contract.EndDate <= noticeLimit));
        var expiringCount = await expiring.CountAsync(cancellationToken);

        var filtered = expiringSoon ? expiring : searched;
        if (status is { } wanted)
        {
            filtered = filtered.Where(employee => employee.Status == wanted);
        }

        // La empresa de la lista es la del contrato vigente: un retirado no tiene empresa.
        if (companyId is { } company)
        {
            filtered = filtered.Where(employee => employee.Contracts.Any(contract =>
                contract.Status == ContractStatus.Active && contract.CompanyId == company));
        }

        var total = await filtered.CountAsync(cancellationToken);
        var items = await WithHistories(filtered
                .OrderBy(employee => employee.LastName)
                .ThenBy(employee => employee.FirstName)
                .ThenBy(employee => employee.Id)
                .Skip((page - 1) * pageSize)
                .Take(pageSize))
            .AsSplitQuery()
            .ToListAsync(cancellationToken);

        return new EmployeeListPage(
            items,
            total,
            CountOf(EmployeeStatus.Active),
            CountOf(EmployeeStatus.Retired),
            CountOf(EmployeeStatus.Deactivated),
            expiringCount);
    }

    public async Task<IReadOnlySet<string>> FindTakenDocumentsAsync(
        Guid tenantId, IReadOnlyCollection<string> documentNumbers, CancellationToken cancellationToken)
    {
        var wanted = documentNumbers.ToArray();
        var taken = await dbContext.Employees
            .AsNoTracking()
            .Where(employee => employee.TenantId == tenantId && wanted.Contains(employee.DocumentNumber))
            .Select(employee => employee.DocumentNumber)
            .ToListAsync(cancellationToken);
        return taken.ToHashSet(StringComparer.Ordinal);
    }

    public async Task<IReadOnlySet<Guid>> ListLinkedMemberIdsAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var linked = await dbContext.Employees
            .AsNoTracking()
            .Where(employee => employee.TenantId == tenantId && employee.MemberId != null)
            .Select(employee => employee.MemberId!.Value)
            .ToListAsync(cancellationToken);
        return linked.ToHashSet();
    }

    public async Task<IReadOnlyList<Employee>> ListForPayrollAsync(
        Guid tenantId, Guid companyId, DateOnly periodStart, DateOnly periodEnd, CancellationToken cancellationToken) =>
        await dbContext.Employees
            .AsNoTracking()
            .Where(employee => employee.TenantId == tenantId
                && employee.Contracts.Any(contract =>
                    contract.CompanyId == companyId
                    && contract.StartDate <= periodEnd
                    && (contract.EndedOn == null || contract.EndedOn >= periodStart)))
            .OrderBy(employee => employee.LastName)
            .ThenBy(employee => employee.FirstName)
            .ThenBy(employee => employee.Id)
            .Include(employee => employee.Contracts).ThenInclude(contract => contract.Salaries)
            .Include(employee => employee.Contracts).ThenInclude(contract => contract.Assignments)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);

    private static IQueryable<Employee> WithHistories(IQueryable<Employee> employees) => employees
        .Include(employee => employee.Contracts).ThenInclude(contract => contract.Salaries)
        .Include(employee => employee.Contracts).ThenInclude(contract => contract.Assignments)
        .Include(employee => employee.Contracts).ThenInclude(contract => contract.Renewals);
}
