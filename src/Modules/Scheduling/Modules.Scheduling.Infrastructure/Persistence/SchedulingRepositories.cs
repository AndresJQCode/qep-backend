using Microsoft.EntityFrameworkCore;
using Modules.Scheduling.Application;
using Modules.Scheduling.Domain;

namespace Modules.Scheduling.Infrastructure.Persistence;

internal sealed class LocationRepository(SchedulingDbContext dbContext) : ILocationRepository
{
    public Task<Location?> FindAsync(Guid tenantId, LocationId id, CancellationToken cancellationToken) =>
        dbContext.Locations.SingleOrDefaultAsync(location => location.TenantId == tenantId && location.Id == id, cancellationToken);

    public Task<Location?> FindMainAsync(Guid tenantId, CancellationToken cancellationToken) =>
        dbContext.Locations.SingleOrDefaultAsync(location => location.TenantId == tenantId && location.IsMain, cancellationToken);

    public async Task<IReadOnlyList<Location>> ListAsync(Guid tenantId, CancellationToken cancellationToken) =>
        await dbContext.Locations.AsNoTracking()
            .Where(location => location.TenantId == tenantId)
            .OrderByDescending(location => location.IsMain)
            .ThenBy(location => location.Name)
            .ThenBy(location => location.Id)
            .ToListAsync(cancellationToken);

    public void Add(Location location) => dbContext.Locations.Add(location);
}

internal sealed class ResourceRepository(SchedulingDbContext dbContext) : IResourceRepository
{
    public Task<Resource?> FindAsync(Guid tenantId, ResourceId id, CancellationToken cancellationToken) =>
        dbContext.Resources.SingleOrDefaultAsync(resource => resource.TenantId == tenantId && resource.Id == id, cancellationToken);

    public async Task<IReadOnlyList<Resource>> FindManyAsync(
        Guid tenantId, IReadOnlyCollection<ResourceId> ids, CancellationToken cancellationToken)
    {
        var wanted = ids.ToArray();
        return await dbContext.Resources.AsNoTracking()
            .Where(resource => resource.TenantId == tenantId && wanted.Contains(resource.Id))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Resource>> ListAsync(Guid tenantId, ResourceScope scope, CancellationToken cancellationToken)
    {
        var query = dbContext.Resources.AsNoTracking().Where(resource => resource.TenantId == tenantId);
        if (!scope.IsAll)
        {
            // Un alcance vacío no es «sin filtro»: Contains sobre una lista vacía no devuelve nada, que es lo correcto.
            var ids = scope.ResourceIds.Select(id => new ResourceId(id)).ToArray();
            query = query.Where(resource => ids.Contains(resource.Id));
        }

        return await query.OrderBy(resource => resource.DisplayName).ThenBy(resource => resource.Id).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Resource>> ListByLocationForUpdateAsync(
        Guid tenantId, LocationId locationId, CancellationToken cancellationToken) =>
        await dbContext.Resources
            .Where(resource => resource.TenantId == tenantId && resource.LocationId == locationId)
            .OrderBy(resource => resource.Id)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Guid>> ListIdsByMemberAsync(Guid tenantId, Guid memberId, CancellationToken cancellationToken)
    {
        var ids = await dbContext.Resources.AsNoTracking()
            .Where(resource => resource.TenantId == tenantId && resource.MemberId == memberId)
            .Select(resource => resource.Id)
            .ToListAsync(cancellationToken);
        return [.. ids.Select(id => id.Value)];
    }

    public Task<bool> MemberIsLinkedAsync(
        Guid tenantId, LocationId locationId, Guid memberId, ResourceId? except, CancellationToken cancellationToken)
    {
        var query = dbContext.Resources.AsNoTracking().Where(
            resource => resource.TenantId == tenantId && resource.LocationId == locationId && resource.MemberId == memberId);
        if (except is { } skipped)
        {
            query = query.Where(resource => resource.Id != skipped);
        }

        return query.AnyAsync(cancellationToken);
    }

    public Task<bool> HasFutureAppointmentsAsync(Guid tenantId, ResourceId id, DateTimeOffset now, CancellationToken cancellationToken) =>
        dbContext.Appointments.AsNoTracking().AnyAsync(
            appointment => appointment.TenantId == tenantId
                && appointment.ResourceId == id
                && appointment.StartsAt > now
                && (appointment.Status == AppointmentStatus.Pending || appointment.Status == AppointmentStatus.Confirmed),
            cancellationToken);

    public void Add(Resource resource) => dbContext.Resources.Add(resource);
}

internal sealed class ServiceRepository(SchedulingDbContext dbContext) : IServiceRepository
{
    public Task<Service?> FindAsync(Guid tenantId, ServiceId id, CancellationToken cancellationToken) =>
        dbContext.Services.SingleOrDefaultAsync(service => service.TenantId == tenantId && service.Id == id, cancellationToken);

    public async Task<IReadOnlyList<Service>> FindManyAsync(
        Guid tenantId, IReadOnlyCollection<ServiceId> ids, CancellationToken cancellationToken)
    {
        var wanted = ids.ToArray();
        return await dbContext.Services.AsNoTracking()
            .Where(service => service.TenantId == tenantId && wanted.Contains(service.Id))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Service>> ListAsync(Guid tenantId, CancellationToken cancellationToken) =>
        await dbContext.Services.AsNoTracking()
            .Where(service => service.TenantId == tenantId)
            .OrderBy(service => service.Name)
            .ThenBy(service => service.Id)
            .ToListAsync(cancellationToken);

    public Task<bool> HasFutureAppointmentsAsync(Guid tenantId, ServiceId id, DateTimeOffset now, CancellationToken cancellationToken) =>
        dbContext.Appointments.AsNoTracking().AnyAsync(
            appointment => appointment.TenantId == tenantId
                && appointment.ServiceId == id
                && appointment.StartsAt > now
                && (appointment.Status == AppointmentStatus.Pending || appointment.Status == AppointmentStatus.Confirmed),
            cancellationToken);

    public void Add(Service service) => dbContext.Services.Add(service);
}

internal sealed class AvailabilityBlockRepository(SchedulingDbContext dbContext) : IAvailabilityBlockRepository
{
    public Task<AvailabilityBlock?> FindAsync(Guid tenantId, AvailabilityBlockId id, CancellationToken cancellationToken) =>
        dbContext.Blocks.SingleOrDefaultAsync(block => block.TenantId == tenantId && block.Id == id, cancellationToken);

    public async Task<IReadOnlyList<AvailabilityBlock>> ListAsync(Guid tenantId, ResourceScope scope, CancellationToken cancellationToken)
    {
        var blocks = await dbContext.Blocks.AsNoTracking()
            .Where(block => block.TenantId == tenantId)
            .ToListAsync(cancellationToken);

        // Sin alcance «todos», sólo las franjas que abren algún recurso suyo. Las listas de ids son columnas uuid[] y se filtran acá:
        // son pocas franjas por tenant (plan, corrección 13). Un alcance vacío no ve ninguna.
        return [.. blocks
            .Where(block => scope.IsAll || block.ResourceIds.Any(resource => scope.Includes(resource.Value)))
            .OrderBy(block => block.Name, StringComparer.Ordinal)
            .ThenBy(block => block.Id.Value)];
    }

    public async Task<IReadOnlyList<AvailabilityBlock>> ListActiveForUpdateAsync(Guid tenantId, CancellationToken cancellationToken) =>
        await dbContext.Blocks
            .Where(block => block.TenantId == tenantId && block.ArchivedAt == null)
            .ToListAsync(cancellationToken);

    public void Add(AvailabilityBlock block) => dbContext.Blocks.Add(block);
}

internal sealed class ClosureRepository(SchedulingDbContext dbContext) : IClosureRepository
{
    public Task<Closure?> FindAsync(Guid tenantId, ClosureId id, CancellationToken cancellationToken) =>
        dbContext.Closures.SingleOrDefaultAsync(closure => closure.TenantId == tenantId && closure.Id == id, cancellationToken);

    public async Task<IReadOnlyList<Closure>> ListAsync(
        Guid tenantId, ResourceScope scope, DateTimeOffset after, CancellationToken cancellationToken)
    {
        var query = dbContext.Closures.AsNoTracking().Where(closure => closure.TenantId == tenantId && closure.EndsAt > after);
        if (!scope.IsAll)
        {
            // Los de toda la sede (recurso nulo) los ve cualquiera: son lo que le cierra la agenda a él también.
            var ids = scope.ResourceIds.Select(id => new ResourceId(id)).ToArray();
            query = query.Where(closure => closure.ResourceId == null || ids.Contains(closure.ResourceId.Value));
        }

        return await query.OrderBy(closure => closure.StartsAt).ThenBy(closure => closure.Id).ToListAsync(cancellationToken);
    }

    public Task<bool> HasActiveAppointmentsAsync(
        Guid tenantId,
        LocationId locationId,
        ResourceId? resourceId,
        DateTimeOffset rangeStart,
        DateTimeOffset rangeFinish,
        CancellationToken cancellationToken)
    {
        // Dos rangos que sólo se tocan en un extremo no se solapan.
        var query = dbContext.Appointments.AsNoTracking().Where(
            appointment => appointment.TenantId == tenantId
                && appointment.StartsAt < rangeFinish
                && rangeStart < appointment.EndsAt
                && (appointment.Status == AppointmentStatus.Pending || appointment.Status == AppointmentStatus.Confirmed));
        query = resourceId is { } resource
            ? query.Where(appointment => appointment.ResourceId == resource)
            : query.Where(appointment => appointment.LocationId == locationId);

        return query.AnyAsync(cancellationToken);
    }

    public void Add(Closure closure) => dbContext.Closures.Add(closure);

    public void Remove(Closure closure) => dbContext.Closures.Remove(closure);
}

internal sealed class SessionRepository(SchedulingDbContext dbContext) : ISessionRepository
{
    public Task<Session?> FindAsync(Guid tenantId, SessionId id, CancellationToken cancellationToken) =>
        dbContext.Sessions.SingleOrDefaultAsync(session => session.TenantId == tenantId && session.Id == id, cancellationToken);

    public Task<Session?> FindByKeyAsync(
        Guid tenantId, ResourceId resourceId, ServiceId serviceId, DateTimeOffset startsAt, CancellationToken cancellationToken) =>
        dbContext.Sessions.SingleOrDefaultAsync(
            session => session.TenantId == tenantId
                && session.ResourceId == resourceId
                && session.ServiceId == serviceId
                && session.StartsAt == startsAt,
            cancellationToken);

    public async Task<IReadOnlyList<Session>> ListOverlappingAsync(
        Guid tenantId,
        IReadOnlyCollection<ResourceId> resourceIds,
        DateTimeOffset rangeStart,
        DateTimeOffset rangeEnd,
        bool onlyActive,
        CancellationToken cancellationToken)
    {
        var wanted = resourceIds.ToArray();
        var query = dbContext.Sessions.AsNoTracking()
            .Where(session => session.TenantId == tenantId
                && wanted.Contains(session.ResourceId)
                && session.StartsAt < rangeEnd
                && session.OccupiesUntil > rangeStart);
        if (onlyActive)
        {
            query = query.Where(session => session.IsActive);
        }

        return await query.OrderBy(session => session.StartsAt).ThenBy(session => session.Id).ToListAsync(cancellationToken);
    }

    public Task<bool> HasAppointmentsAsync(Guid tenantId, SessionId id, CancellationToken cancellationToken) =>
        dbContext.Appointments.AsNoTracking().AnyAsync(
            appointment => appointment.TenantId == tenantId && appointment.SessionId == id, cancellationToken);

    public Task<bool> HasLiveAppointmentsAsync(Guid tenantId, SessionId id, CancellationToken cancellationToken) =>
        dbContext.Appointments.AsNoTracking().AnyAsync(
            appointment => appointment.TenantId == tenantId
                && appointment.SessionId == id
                && appointment.Status != AppointmentStatus.Cancelled,
            cancellationToken);

    public void Add(Session session) => dbContext.Sessions.Add(session);

    public void Remove(Session session) => dbContext.Sessions.Remove(session);
}

internal sealed class SchedulingSettingsRepository(SchedulingDbContext dbContext) : ISchedulingSettingsRepository
{
    public Task<SchedulingSettings?> FindAsync(Guid tenantId, CancellationToken cancellationToken) =>
        dbContext.Settings.SingleOrDefaultAsync(settings => settings.TenantId == tenantId, cancellationToken);
}

internal sealed class SchedulingSetupReader(SchedulingDbContext dbContext) : ISchedulingSetupReader
{
    public async Task<SchedulingSetupSnapshot> ReadAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var activeLocations = await dbContext.Locations.AsNoTracking()
            .CountAsync(location => location.TenantId == tenantId && location.ArchivedAt == null, cancellationToken);
        var activeResources = await dbContext.Resources.AsNoTracking()
            .CountAsync(resource => resource.TenantId == tenantId && resource.ArchivedAt == null, cancellationToken);
        var hasService = await dbContext.Services.AsNoTracking()
            .AnyAsync(service => service.TenantId == tenantId && service.ArchivedAt == null, cancellationToken);
        var hasBlock = await dbContext.Blocks.AsNoTracking()
            .AnyAsync(block => block.TenantId == tenantId && block.ArchivedAt == null, cancellationToken);

        return new SchedulingSetupSnapshot(hasService, activeResources > 0, hasBlock, activeLocations, activeResources);
    }
}

internal sealed class AppointmentRepository(SchedulingDbContext dbContext) : IAppointmentRepository
{
    public Task<Appointment?> FindAsync(Guid tenantId, AppointmentId id, CancellationToken cancellationToken) =>
        dbContext.Appointments
            .Include(appointment => appointment.Events)
            .SingleOrDefaultAsync(appointment => appointment.TenantId == tenantId && appointment.Id == id, cancellationToken);

    public Task RefreshAsync(Appointment appointment, CancellationToken cancellationToken) =>
        dbContext.Entry(appointment).ReloadAsync(cancellationToken);

    public void Discard() => dbContext.ChangeTracker.Clear();

    public Task<Appointment?> FindFreshAsync(Guid tenantId, AppointmentId id, CancellationToken cancellationToken)
    {
        // The failed save left the entity with the change that lost: reading through the tracker would hand it back as if it had won.
        dbContext.ChangeTracker.Clear();
        return FindAsync(tenantId, id, cancellationToken);
    }

    public Task<Appointment?> FindByClientRequestAsync(Guid tenantId, string clientRequestId, CancellationToken cancellationToken) =>
        dbContext.Appointments
            .Include(appointment => appointment.Events)
            .SingleOrDefaultAsync(
                appointment => appointment.TenantId == tenantId && appointment.ClientRequestId == clientRequestId, cancellationToken);

    public async Task LockResourcesAsync(Guid tenantId, IReadOnlyCollection<ResourceId> resourceIds, CancellationToken cancellationToken)
    {
        // Sorted by id so two bookings asking for the same resources lock them in the same order and never wait on each other.
        var ids = resourceIds.Select(id => id.Value).Distinct().Order().ToArray();
        if (ids.Length == 0)
        {
            return;
        }

        // The rows are read only to be locked: the lock lasts until the transaction of the unit of work ends.
        await dbContext.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value" FROM scheduling.resources
            WHERE tenant_id = {tenantId} AND id = ANY({ids})
            ORDER BY id
            FOR UPDATE
            """).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Appointment>> ListFutureLiveAsync(
        Guid tenantId, IReadOnlyCollection<ResourceId> resourceIds, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (resourceIds.Count == 0)
        {
            return [];
        }

        var ids = resourceIds.ToArray();
        return await dbContext.Appointments
            .Include(appointment => appointment.Events)
            .Where(appointment => appointment.TenantId == tenantId
                && ids.Contains(appointment.ResourceId)
                && appointment.StartsAt > now
                && (appointment.Status == AppointmentStatus.Pending || appointment.Status == AppointmentStatus.Confirmed))
            .OrderBy(appointment => appointment.StartsAt)
            .ThenBy(appointment => appointment.Id)
            .ToListAsync(cancellationToken);
    }

    public void Add(Appointment appointment) => dbContext.Appointments.Add(appointment);
}

internal sealed class AppointmentReadRepository(SchedulingDbContext dbContext) : IAppointmentReadRepository
{
    // The scope is applied in the query: an empty scope is not "no filter", it matches nothing (Contains over an empty list).
    private IQueryable<Appointment> InScope(Guid tenantId, ResourceScope scope)
    {
        var query = dbContext.Appointments.AsNoTracking().Where(appointment => appointment.TenantId == tenantId);
        if (scope.IsAll)
        {
            return query;
        }

        var ids = scope.ResourceIds.Select(id => new ResourceId(id)).ToArray();
        return query.Where(appointment => ids.Contains(appointment.ResourceId));
    }

    public async Task<IReadOnlyList<Appointment>> ListInRangeAsync(
        Guid tenantId,
        ResourceScope scope,
        IReadOnlyCollection<ResourceId> resourceIds,
        DateTimeOffset rangeStart,
        DateTimeOffset rangeEnd,
        CancellationToken cancellationToken)
    {
        if (resourceIds.Count == 0)
        {
            return [];
        }

        var wanted = resourceIds.ToArray();
        return await InScope(tenantId, scope)
            .Where(appointment => appointment.Status != AppointmentStatus.Cancelled
                && wanted.Contains(appointment.ResourceId)
                && appointment.StartsAt < rangeEnd
                && appointment.EndsAt > rangeStart)
            .OrderBy(appointment => appointment.StartsAt)
            .ThenBy(appointment => appointment.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Appointment>> ListUpcomingByCustomerAsync(
        Guid tenantId, Guid customerId, DateTimeOffset now, int take, CancellationToken cancellationToken) =>
        await dbContext.Appointments.AsNoTracking()
            .Where(appointment => appointment.TenantId == tenantId
                && appointment.CustomerId == customerId
                && appointment.StartsAt >= now
                && (appointment.Status == AppointmentStatus.Pending || appointment.Status == AppointmentStatus.Confirmed))
            .OrderBy(appointment => appointment.StartsAt)
            .ThenBy(appointment => appointment.Id)
            .Take(take)
            .ToListAsync(cancellationToken);

    public async Task<DeskSnapshot> ReadDeskAsync(
        Guid tenantId,
        ResourceScope scope,
        DateTimeOffset dayStart,
        DateTimeOffset dayEnd,
        DateTimeOffset now,
        int take,
        CancellationToken cancellationToken)
    {
        var inScope = InScope(tenantId, scope);
        var day = inScope.Where(appointment => appointment.StartsAt >= dayStart
            && appointment.StartsAt < dayEnd
            && appointment.Status != AppointmentStatus.Cancelled);

        var byStatus = await day
            .GroupBy(appointment => appointment.Status)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);
        var upcoming = await day
            .Where(appointment => (appointment.Status == AppointmentStatus.Pending || appointment.Status == AppointmentStatus.Confirmed)
                && appointment.EndsAt >= now)
            .OrderBy(appointment => appointment.StartsAt)
            .ThenBy(appointment => appointment.Id)
            .Take(take)
            .ToListAsync(cancellationToken);

        // The longest-waiting request first: the queue is worked from the top.
        var pendingQuery = inScope.Where(appointment => appointment.Status == AppointmentStatus.Pending);
        var pendingTotal = await pendingQuery.CountAsync(cancellationToken);
        var pending = await pendingQuery
            .OrderBy(appointment => appointment.CreatedAt)
            .ThenBy(appointment => appointment.Id)
            .Take(take)
            .ToListAsync(cancellationToken);

        // The most recent first: it is the one the secretary remembers to mark.
        var unmarkedQuery = inScope.Where(DeskRules.Unmarked(now));
        var unmarkedTotal = await unmarkedQuery.CountAsync(cancellationToken);
        var unmarked = await unmarkedQuery
            .OrderByDescending(appointment => appointment.EndsAt)
            .ThenBy(appointment => appointment.Id)
            .Take(take)
            .ToListAsync(cancellationToken);

        int CountOf(AppointmentStatus status) => byStatus.Where(row => row.Status == status).Sum(row => row.Count);

        return new DeskSnapshot(
            byStatus.Sum(row => row.Count),
            CountOf(AppointmentStatus.Completed),
            CountOf(AppointmentStatus.NoShow),
            pendingTotal,
            pending,
            unmarkedTotal,
            unmarked,
            upcoming);
    }
}
