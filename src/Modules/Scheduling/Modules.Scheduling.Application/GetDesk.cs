using BuildingBlocks.Application;
using Modules.Scheduling.Domain;
using Modules.Tenancy.Application;

namespace Modules.Scheduling.Application;

/// <param name="Date">Nulo es hoy en la zona horaria del tenant.</param>
public sealed record GetDeskQuery(Guid TenantId, DateOnly? Date) : IQuery<DeskDto>;

/// <summary>
/// El escritorio de la secretaria (plan 2026-11-21, Tarea 10). Todo sale del alcance de quien pregunta: las consultas de citas lo aplican en
/// la base y los huecos libres se cuentan sólo sobre sus recursos, así que los contadores de un médico son los de su agenda y no los del
/// consultorio. «Ahora» y «hoy» son los del tenant (<see cref="ITenantClock"/>), no los del servidor.
/// </summary>
public sealed class GetDeskHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    SchedulingFirstAccess firstAccess,
    ResourceScopeResolver scopeResolver,
    IResourceRepository resources,
    IServiceRepository services,
    IAppointmentReadRepository appointments,
    ISchedulingCustomerLookup customers,
    AvailabilityLoader loader)
    : IQueryHandler<GetDeskQuery, DeskDto>
{
    /// <summary>Las filas máximas de cada cola. Alcanza para trabajar el día; el total viaja en los contadores.</summary>
    public const int QueueLimit = 50;

    public async Task<DeskDto> HandleAsync(GetDeskQuery query, CancellationToken cancellationToken)
    {
        await SchedulingAuthorization.EnsureAsync(
            executionContext, tenantModules, query.TenantId, cancellationToken, SchedulingPermissions.AppointmentRead);
        await firstAccess.EnsureAsync(query.TenantId, cancellationToken);

        var scope = await scopeResolver.ResolveAsync(query.TenantId, cancellationToken);
        var calendar = await tenantClock.GetAsync(query.TenantId, cancellationToken);
        var date = query.Date ?? calendar.Today;
        var loaded = await loader.LoadAsync(query.TenantId, scope, calendar, date, date, cancellationToken);

        var snapshot = await appointments.ReadDeskAsync(
            query.TenantId, scope, loaded.RangeStart, loaded.RangeEnd, calendar.UtcNow, QueueLimit, cancellationToken);

        // Los huecos libres del día, sólo de los recursos del alcance (el cargador ya los filtró).
        var serviceList = await services.ListAsync(query.TenantId, cancellationToken);
        var sessions = await loader.SessionsAsync(query.TenantId, loaded, loaded.OfferedResourceIds, onlyActive: false, cancellationToken);
        var free = FreeSlotCounter.Count(loaded, serviceList, sessions).Values.Sum();

        // Incluye los recursos archivados: una cita vencida sin marcar puede ser de uno que se archivó después.
        var resourceNames = (await resources.ListAsync(query.TenantId, scope, cancellationToken))
            .ToDictionary(resource => resource.Id, resource => resource.DisplayName);
        var serviceNames = serviceList.ToDictionary(service => service.Id, service => service.Name);
        var shown = snapshot.Pending.Concat(snapshot.Unmarked).Concat(snapshot.Upcoming);
        var customerNames = await customers.FindNamesAsync(
            query.TenantId, [.. shown.Select(row => row.CustomerId).Distinct()], cancellationToken);

        DeskItemDto Item(Appointment row) => new(
            row.Id.Value,
            customerNames.GetValueOrDefault(row.CustomerId, AppointmentComposer.UnknownCustomer),
            serviceNames.GetValueOrDefault(row.ServiceId, string.Empty),
            resourceNames.GetValueOrDefault(row.ResourceId, string.Empty),
            row.StartsAt,
            row.Source.ToString(),
            DeskRules.RequestedAgoMinutes(row.CreatedAt, calendar.UtcNow));

        return new DeskDto(
            date,
            new DeskCountersDto(
                snapshot.DayAppointments, snapshot.PendingTotal, snapshot.UnmarkedTotal, snapshot.Attended, snapshot.NoShow, free),
            [.. snapshot.Pending.Select(Item)],
            [.. snapshot.Unmarked.Select(Item)],
            [.. snapshot.Upcoming.Select(Item)]);
    }
}
