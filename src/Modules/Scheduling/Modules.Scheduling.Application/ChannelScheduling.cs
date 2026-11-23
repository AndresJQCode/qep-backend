using BuildingBlocks.Application;
using FluentValidation;
using Modules.Scheduling.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Scheduling.Application;

/// <summary>El módulo prendido y cuántos servicios se pueden reservar por canal: lo que el editor del agente necesita para publicar (spec 2026-11-23 §9.2).</summary>
public sealed record ChannelReadiness(bool ModuleEnabled, int BookableServices);

/// <param name="TimeZone">El id IANA de la zona del tenant: una sola por resultado (D-10).</param>
public sealed record ChannelBookableService(Guid ServiceId, string Name, int DurationMinutes);

public sealed record ChannelServiceList(string TimeZone, IReadOnlyList<ChannelBookableService> Services, bool Truncated);

public sealed record ChannelSlotResource(Guid ResourceId, string Name);

/// <param name="StartsAt">En la zona del tenant (el offset es el de esa zona), no en UTC: el agente lee la hora tal como la ve el cliente.</param>
/// <param name="Resources">Sólo los recursos que la franja deja elegir al cliente (D-11); vacío cuando la agenda asigna el primero libre.</param>
public sealed record ChannelSlot(DateTimeOffset StartsAt, DateTimeOffset EndsAt, int SeatsLeft, IReadOnlyList<ChannelSlotResource> Resources);

public sealed record ChannelAvailability(string TimeZone, Guid ServiceId, IReadOnlyList<ChannelSlot> Slots, bool Truncated);

/// <param name="CanChangeByChat">Está abierta y todavía dentro del plazo del cliente: puede cancelarla por chat.</param>
/// <param name="CanRescheduleByChat">Además de lo anterior, su servicio se reserva por canal (D-15): una persona mueve las demás.</param>
/// <param name="ChangeByChatUntil">El último instante en que el cliente puede cambiarla por su cuenta, en la zona del tenant.</param>
public sealed record ChannelAppointment(
    Guid AppointmentId,
    string Status,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string TimeZone,
    Guid ServiceId,
    string ServiceName,
    string ResourceName,
    string LocationName,
    bool CanChangeByChat,
    bool CanRescheduleByChat,
    DateTimeOffset ChangeByChatUntil);

public sealed record ChannelAppointmentList(string TimeZone, IReadOnlyList<ChannelAppointment> Appointments, bool Truncated);

/// <summary>Lo que comprobaría la reserva de ese inicio, sin escribir: la vista previa del playground (D-7).</summary>
public sealed record ChannelStartCheck(
    string Status,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string TimeZone,
    string ServiceName,
    string ResourceName,
    string LocationName);

/// <summary>
/// El único camino para operar la Agenda sin sesión (spec 2026-11-23 §6.3), con la doctrina de <c>IExternalQuotationWriter</c>: un módulo
/// consumidor declara su propio puerto y su adaptador en Bootstrapper llama a éste. Cada método comprueba el módulo <c>scheduling</c>
/// (<c>tenancy.module_not_enabled</c>, salvo <see cref="GetReadinessAsync"/>), aprovisiona el primer acceso y actúa sobre
/// <see cref="ResourceScope.All"/>: el agente atiende la agenda entera del tenant, el alcance por miembro no aplica a un cliente. Las horas
/// entran como <see cref="DateTime"/> local sin zona y salen como <see cref="DateTimeOffset"/> en la zona del tenant (D-10).
/// </summary>
public interface IChannelScheduling
{
    /// <summary>No lanza con el módulo apagado: responde <c>ModuleEnabled: false</c>.</summary>
    Task<ChannelReadiness> GetReadinessAsync(Guid tenantId, CancellationToken cancellationToken);

    Task<ChannelServiceList> ListBookableServicesAsync(Guid tenantId, int take, CancellationToken cancellationToken);

    Task<ChannelAvailability> SearchAvailabilityAsync(
        Guid tenantId, Guid serviceId, DateOnly fromDate, DateOnly toDate, Guid? resourceId, int take, CancellationToken cancellationToken);

    Task<ChannelAppointmentList> ListCustomerAppointmentsAsync(Guid tenantId, Guid customerId, int take, CancellationToken cancellationToken);

    /// <summary>Una cita del cliente en cualquier estado; <c>null</c> si no existe o es de otro cliente (el mismo silencio, para no confirmar ids ajenos).</summary>
    Task<ChannelAppointment?> FindCustomerAppointmentAsync(
        Guid tenantId, Guid customerId, Guid appointmentId, CancellationToken cancellationToken);

    /// <summary>Lanza lo mismo que la reserva lanzaría, sin candado ni escritura.</summary>
    Task<ChannelStartCheck> CheckStartAsync(
        Guid tenantId, Guid serviceId, DateTime localStartsAt, Guid? resourceId, CancellationToken cancellationToken);
}

public sealed class ChannelScheduling(
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    SchedulingFirstAccess firstAccess,
    IServiceRepository services,
    IResourceRepository resources,
    ILocationRepository locations,
    IAvailabilityBlockRepository blocks,
    ISchedulingSettingsRepository settings,
    IAppointmentReadRepository appointmentReads,
    IAppointmentRepository appointments,
    AvailabilitySearch search,
    AvailabilityLoader loader)
    : IChannelScheduling
{
    public async Task<ChannelReadiness> GetReadinessAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        // Con el tenant simulado por el stub (sin máscara) tampoco bloquea, igual que TenantModuleGuard.
        var modules = await tenantModules.FindAsync(tenantId, cancellationToken);
        if (modules is not null && !modules.IsEnabled(TenantModuleKeys.Scheduling))
        {
            return new ChannelReadiness(ModuleEnabled: false, BookableServices: 0);
        }

        await firstAccess.EnsureAsync(tenantId, cancellationToken);
        return new ChannelReadiness(ModuleEnabled: true, (await BookableServicesAsync(tenantId, cancellationToken)).Count);
    }

    public async Task<ChannelServiceList> ListBookableServicesAsync(Guid tenantId, int take, CancellationToken cancellationToken)
    {
        await EnsureAsync(tenantId, cancellationToken);
        var bookable = await BookableServicesAsync(tenantId, cancellationToken);
        var calendar = await tenantClock.GetAsync(tenantId, cancellationToken);
        return new ChannelServiceList(
            calendar.TimeZone.Id,
            [.. bookable.Take(take).Select(service => new ChannelBookableService(service.Id.Value, service.Name, service.DurationMinutes))],
            bookable.Count > take);
    }

    public async Task<ChannelAvailability> SearchAvailabilityAsync(
        Guid tenantId, Guid serviceId, DateOnly fromDate, DateOnly toDate, Guid? resourceId, int take, CancellationToken cancellationToken)
    {
        await EnsureAsync(tenantId, cancellationToken);
        new AvailabilityRequestValidator().ValidateAndThrow(new AvailabilityRequest(serviceId, fromDate, toDate, resourceId));
        var service = await BookableServiceAsync(tenantId, serviceId, cancellationToken);

        ResourceId? onlyResource = null;
        if (resourceId is { } requested)
        {
            var resource = await resources.FindAsync(tenantId, new ResourceId(requested), cancellationToken);
            if (resource is null || resource.Archived)
            {
                throw SchedulingNotFound.Resource(requested);
            }

            onlyResource = resource.Id;
        }

        var found = await search.SearchAsync(tenantId, ResourceScope.All, service, fromDate, toDate, onlyResource, cancellationToken);

        // A resource asked for by name must be one the block lets the client choose somewhere in the range (D-16): otherwise the caller is
        // asking for something it can never book, and an empty list would only hide that.
        if (onlyResource is not null
            && found.Slots.Count > 0
            && !found.Slots.Any(slot => slot.Options.Any(option => option.AllowClientChoice)))
        {
            throw NotChoosable();
        }

        var slots = found.Slots.Take(take).Select(slot => new ChannelSlot(
            TimeZoneInfo.ConvertTime(slot.StartsAt, found.TimeZone),
            TimeZoneInfo.ConvertTime(slot.EndsAt, found.TimeZone),
            slot.SeatsLeft,
            [.. slot.Options
                .Where(option => option.AllowClientChoice)
                .Select(option => new ChannelSlotResource(option.ResourceId.Value, found.ResourceNames[option.ResourceId]))
                .DistinctBy(item => item.ResourceId)]));
        return new ChannelAvailability(found.TimeZone.Id, service.Id.Value, [.. slots], found.Slots.Count > take);
    }

    public async Task<ChannelAppointmentList> ListCustomerAppointmentsAsync(
        Guid tenantId, Guid customerId, int take, CancellationToken cancellationToken)
    {
        await EnsureAsync(tenantId, cancellationToken);
        var calendar = await tenantClock.GetAsync(tenantId, cancellationToken);
        var upcoming = await appointmentReads.ListUpcomingByCustomerAsync(tenantId, customerId, calendar.UtcNow, take + 1, cancellationToken);
        return new ChannelAppointmentList(
            calendar.TimeZone.Id, await DescribeAsync(tenantId, [.. upcoming.Take(take)], calendar, cancellationToken), upcoming.Count > take);
    }

    public async Task<ChannelAppointment?> FindCustomerAppointmentAsync(
        Guid tenantId, Guid customerId, Guid appointmentId, CancellationToken cancellationToken)
    {
        await EnsureAsync(tenantId, cancellationToken);
        var appointment = await appointments.FindAsync(tenantId, new AppointmentId(appointmentId), cancellationToken);
        if (appointment is null || appointment.CustomerId != customerId)
        {
            return null;
        }

        var calendar = await tenantClock.GetAsync(tenantId, cancellationToken);
        return (await DescribeAsync(tenantId, [appointment], calendar, cancellationToken))[0];
    }

    public async Task<ChannelStartCheck> CheckStartAsync(
        Guid tenantId, Guid serviceId, DateTime localStartsAt, Guid? resourceId, CancellationToken cancellationToken)
    {
        await EnsureAsync(tenantId, cancellationToken);
        var service = await BookableServiceAsync(tenantId, serviceId, cancellationToken);
        var calendar = await tenantClock.GetAsync(tenantId, cancellationToken);
        var startsAt = ToInstant(calendar, localStartsAt);

        ResourceId? requested = null;
        if (resourceId is { } chosen)
        {
            var resource = await resources.FindAsync(tenantId, new ResourceId(chosen), cancellationToken);
            if (resource is null || resource.Archived)
            {
                SchedulingFieldErrors.Throw("resourceId", "El recurso no existe en esta organización o está archivado.");
            }

            requested = resource.Id;
        }

        var loaded = await loader.LoadForStartAsync(tenantId, ResourceScope.All, startsAt, cancellationToken);
        var rulesSessions = await loader.SessionsAsync(tenantId, loaded, loaded.OfferedResourceIds, onlyActive: false, cancellationToken);
        var candidates = AppointmentStarts.RulesCandidates(loaded, service, rulesSessions, startsAt, requested, _ => true);
        if (requested is { } mustBeChoosable)
        {
            AppointmentStarts.EnsureClientChoice(loaded, service, rulesSessions, startsAt, mustBeChoosable);
        }

        // No lock here: this is the question the booking asks, not the booking. The seat can still be gone when the real call comes.
        var fresh = await loader.SessionsAsync(tenantId, loaded, candidates, onlyActive: true, cancellationToken);
        var option = AppointmentStarts.PickFree(loaded, service, fresh, startsAt, candidates, preferred: null);
        var offered = loaded.OfferedResources.Single(resource => resource.Id == option.ResourceId);
        var location = await locations.FindAsync(tenantId, offered.LocationId, cancellationToken);

        return new ChannelStartCheck(
            (loaded.Rules.ConfirmationMode == ConfirmationMode.Automatic ? AppointmentStatus.Confirmed : AppointmentStatus.Pending).ToString(),
            calendar.ToLocal(startsAt),
            calendar.ToLocal(startsAt.AddMinutes(service.DurationMinutes)),
            calendar.TimeZone.Id,
            service.Name,
            offered.DisplayName,
            location?.Name ?? string.Empty);
    }

    private async Task EnsureAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        await TenantModuleGuard.EnsureEnabledAsync(tenantModules, tenantId, TenantModuleKeys.Scheduling, cancellationToken);
        await firstAccess.EnsureAsync(tenantId, cancellationToken);
    }

    /// <summary>
    /// Reservable: activo, con <c>bookableByChannel</c> y ofrecido por alguna franja no archivada con algún recurso activo. Una sola función
    /// para el conteo del editor y para la lista del agente, así que ninguna de las dos promete lo que la otra no ofrece.
    /// </summary>
    private async Task<IReadOnlyList<Service>> BookableServicesAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var activeResources = (await resources.ListAsync(tenantId, ResourceScope.All, cancellationToken))
            .Where(resource => !resource.Archived)
            .Select(resource => resource.Id)
            .ToHashSet();
        var offered = (await blocks.ListAsync(tenantId, ResourceScope.All, cancellationToken))
            .Where(block => !block.Archived && block.ResourceIds.Any(activeResources.Contains))
            .SelectMany(block => block.ServiceIds)
            .ToHashSet();
        return [.. (await services.ListAsync(tenantId, cancellationToken))
            .Where(service => !service.Archived && service.BookableByChannel && offered.Contains(service.Id))];
    }

    private async Task<Service> BookableServiceAsync(Guid tenantId, Guid serviceId, CancellationToken cancellationToken)
    {
        var service = (await services.FindManyAsync(tenantId, [new ServiceId(serviceId)], cancellationToken)).SingleOrDefault();
        if (service is null || service.Archived || !service.BookableByChannel)
        {
            SchedulingFieldErrors.Throw("serviceId", "El servicio no existe, está archivado o no se reserva por WhatsApp.");
        }

        return service;
    }

    private static DateTimeOffset ToInstant(TenantCalendar calendar, DateTime localStartsAt)
    {
        var local = DateTime.SpecifyKind(localStartsAt, DateTimeKind.Unspecified);

        // The clocks skipped this hour: no instant has it, and guessing one would book a time the client never asked for.
        if (calendar.TimeZone.IsInvalidTime(local))
        {
            throw new SchedulingDomainException(SchedulingErrorCodes.OutsideRules, AppointmentStarts.OutsideRulesMessage);
        }

        return new DateTimeOffset(local, calendar.TimeZone.GetUtcOffset(local)).ToUniversalTime();
    }

    internal static SchedulingDomainException NotChoosable() =>
        new(SchedulingErrorCodes.ResourceNotChoosable, AppointmentStarts.ResourceNotChoosableMessage);

    private async Task<IReadOnlyList<ChannelAppointment>> DescribeAsync(
        Guid tenantId, IReadOnlyList<Appointment> items, TenantCalendar calendar, CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return [];
        }

        var rules = await settings.FindAsync(tenantId, cancellationToken)
            ?? throw new InvalidOperationException("The scheduling rules row is missing: first access provisions it before any read.");
        var serviceById = (await services.FindManyAsync(tenantId, [.. items.Select(item => item.ServiceId).Distinct()], cancellationToken))
            .ToDictionary(service => service.Id);
        var resourceById = (await resources.FindManyAsync(tenantId, [.. items.Select(item => item.ResourceId).Distinct()], cancellationToken))
            .ToDictionary(resource => resource.Id);
        var locationById = (await locations.ListAsync(tenantId, cancellationToken)).ToDictionary(location => location.Id);

        return [.. items.Select(item =>
        {
            var service = serviceById[item.ServiceId];
            var open = item.Status is AppointmentStatus.Pending or AppointmentStatus.Confirmed;
            var canChange = open && AppointmentCancellationRules.ClientMayCancel(item.StartsAt, calendar.UtcNow, rules.ClientCancelCutoffHours);
            return new ChannelAppointment(
                item.Id.Value,
                item.Status.ToString(),
                calendar.ToLocal(item.StartsAt),
                calendar.ToLocal(item.EndsAt),
                calendar.TimeZone.Id,
                service.Id.Value,
                service.Name,
                resourceById[item.ResourceId].DisplayName,
                locationById.TryGetValue(item.LocationId, out var location) ? location.Name : string.Empty,
                canChange,
                canChange && service.BookableByChannel,
                calendar.ToLocal(AppointmentCancellationRules.ChangeByChatUntil(item.StartsAt, rules.ClientCancelCutoffHours)));
        })];
    }
}
