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
