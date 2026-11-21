using Microsoft.EntityFrameworkCore;
using Modules.Hr.Application;
using Modules.Hr.Domain;

namespace Modules.Hr.Infrastructure.Persistence;

internal sealed class HrSettingsRepository(HrDbContext dbContext) : IHrSettingsRepository
{
    public Task<Site?> FindSiteAsync(Guid tenantId, SiteId id, CancellationToken cancellationToken) =>
        dbContext.Sites.SingleOrDefaultAsync(site => site.TenantId == tenantId && site.Id == id, cancellationToken);

    public Task<Workplace?> FindWorkplaceAsync(Guid tenantId, WorkplaceId id, CancellationToken cancellationToken) =>
        dbContext.Workplaces.SingleOrDefaultAsync(workplace => workplace.TenantId == tenantId && workplace.Id == id, cancellationToken);

    public Task<Area?> FindAreaAsync(Guid tenantId, AreaId id, CancellationToken cancellationToken) =>
        dbContext.Areas.SingleOrDefaultAsync(area => area.TenantId == tenantId && area.Id == id, cancellationToken);

    public Task<Position?> FindPositionAsync(Guid tenantId, PositionId id, CancellationToken cancellationToken) =>
        dbContext.Positions.SingleOrDefaultAsync(position => position.TenantId == tenantId && position.Id == id, cancellationToken);

    public void Add(Site site) => dbContext.Sites.Add(site);

    public void Add(Workplace workplace) => dbContext.Workplaces.Add(workplace);

    public void Add(Area area) => dbContext.Areas.Add(area);

    public void Add(Position position) => dbContext.Positions.Add(position);

    public void Remove(Site site) => dbContext.Sites.Remove(site);

    public void Remove(Workplace workplace) => dbContext.Workplaces.Remove(workplace);

    public void Remove(Area area) => dbContext.Areas.Remove(area);

    public void Remove(Position position) => dbContext.Positions.Remove(position);

    public async Task<HrSettingsSnapshot> ListAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var sites = await dbContext.Sites.AsNoTracking().Where(site => site.TenantId == tenantId).ToListAsync(cancellationToken);
        var workplaces = await dbContext.Workplaces.AsNoTracking().Where(workplace => workplace.TenantId == tenantId).ToListAsync(cancellationToken);
        var areas = await dbContext.Areas.AsNoTracking().Where(area => area.TenantId == tenantId).ToListAsync(cancellationToken);
        var positions = await dbContext.Positions.AsNoTracking().Where(position => position.TenantId == tenantId).ToListAsync(cancellationToken);

        // Lo que algún registro de cargo y lugar de este tenant referencia: restrict en la base, así que ni se elimina.
        var assignments = TenantAssignments(tenantId);
        var inUse = new HashSet<Guid>();
        foreach (var id in await assignments.Select(assignment => assignment.PositionId).Distinct().ToListAsync(cancellationToken))
        {
            inUse.Add(id.Value);
        }

        foreach (var id in await assignments.Select(assignment => assignment.AreaId).Distinct().ToListAsync(cancellationToken))
        {
            if (id is { } area)
            {
                inUse.Add(area.Value);
            }
        }

        foreach (var id in await assignments.Select(assignment => assignment.SiteId).Distinct().ToListAsync(cancellationToken))
        {
            inUse.Add(id.Value);
        }

        foreach (var id in await assignments.Select(assignment => assignment.WorkplaceId).Distinct().ToListAsync(cancellationToken))
        {
            inUse.Add(id.Value);
        }

        // Una sede con algún centro (archivado o no) tampoco se elimina.
        foreach (var workplace in workplaces)
        {
            inUse.Add(workplace.SiteId.Value);
        }

        return new HrSettingsSnapshot(sites, workplaces, areas, positions, inUse);
    }

    public async Task<bool> IsInUseAsync(Guid tenantId, Guid settingId, CancellationToken cancellationToken)
    {
        var position = new PositionId(settingId);
        var area = new AreaId(settingId);
        var site = new SiteId(settingId);
        var workplace = new WorkplaceId(settingId);

        var referenced = await TenantAssignments(tenantId).AnyAsync(
            assignment => assignment.PositionId == position
                || assignment.AreaId == area
                || assignment.SiteId == site
                || assignment.WorkplaceId == workplace,
            cancellationToken);
        return referenced
            || await dbContext.Workplaces.AnyAsync(candidate => candidate.TenantId == tenantId && candidate.SiteId == site, cancellationToken);
    }

    public Task<bool> SiteHasActiveWorkplacesAsync(Guid tenantId, SiteId siteId, CancellationToken cancellationToken) =>
        dbContext.Workplaces.AnyAsync(
            workplace => workplace.TenantId == tenantId && workplace.SiteId == siteId && workplace.ArchivedAt == null,
            cancellationToken);

    public async Task<bool> IsSetupCompleteAsync(
        Guid tenantId, IReadOnlyCollection<Guid> activeCompanyIds, CancellationToken cancellationToken)
    {
        if (activeCompanyIds.Count == 0)
        {
            return false;
        }

        var hasPosition = await dbContext.Positions.AnyAsync(
            position => position.TenantId == tenantId && position.ArchivedAt == null, cancellationToken);
        if (!hasPosition)
        {
            return false;
        }

        return await (
            from workplace in dbContext.Workplaces
            join site in dbContext.Sites on workplace.SiteId equals site.Id
            where workplace.TenantId == tenantId
                && workplace.ArchivedAt == null
                && site.ArchivedAt == null
                && activeCompanyIds.Contains(site.CompanyId)
            select workplace.Id).AnyAsync(cancellationToken);
    }

    // Los registros no llevan tenant: se llega por el contrato y el empleado.
    private IQueryable<AssignmentRecord> TenantAssignments(Guid tenantId) =>
        from assignment in dbContext.AssignmentRecords.AsNoTracking()
        join contract in dbContext.Contracts on assignment.ContractId equals contract.Id
        join employee in dbContext.Employees on contract.EmployeeId equals employee.Id
        where employee.TenantId == tenantId
        select assignment;
}
