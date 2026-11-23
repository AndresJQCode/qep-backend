using BuildingBlocks.Application;
using FluentValidation;
using Modules.Scheduling.Domain;
using Modules.Tenancy.Application;

namespace Modules.Scheduling.Application;

/// <summary>
/// Los parámetros de <c>GET calendar</c>: <c>date</c> (un día) o <c>from</c> y <c>to</c> (un rango), ambos extremos inclusive y en el
/// calendario del tenant. Todo es anulable para que el validador devuelva el campo y no un 400 de binding.
/// </summary>
/// <param name="ResourceIds">Si vienen, sólo esos recursos; uno fuera del alcance de quien pide responde 403 y uno que no existe, 404.</param>
public sealed record CalendarRequest(DateOnly? Date, DateOnly? From, DateOnly? To, Guid? LocationId, IReadOnlyList<Guid>? ResourceIds);

public sealed class CalendarRequestValidator : SchedulingValidator<CalendarRequest>
{
    /// <summary>El mismo tope de <see cref="AvailabilityRequestValidator.MaxDays"/>: dos meses alcanzan para el calendario y acotan el trabajo
    /// del motor de disponibilidad, que se corre una vez por servicio.</summary>
    public const int MaxDays = AvailabilityRequestValidator.MaxDays;

    public CalendarRequestValidator()
    {
        RuleFor(request => request.Date)
            .Must(date => date is not null).WithMessage("Indica el día o el rango que quieres ver.")
            .When(request => request.From is null && request.To is null);
        RuleFor(request => request.Date)
            .Must(date => date is null).WithMessage("Pide un día o un rango, no los dos.")
            .When(request => request.From is not null || request.To is not null);
        RuleFor(request => request.From)
            .NotNull().WithMessage("Indica desde qué día.")
            .When(request => request.Date is null && request.To is not null);
        RuleFor(request => request.To)
            .NotNull().WithMessage("Indica hasta qué día.")
            .When(request => request.Date is null && request.From is not null);
        RuleFor(request => request.To)
            .Must((request, to) => to >= request.From).WithMessage("El rango no puede terminar antes de empezar.")
            .Must((request, to) => to!.Value.DayNumber - request.From!.Value.DayNumber <= MaxDays)
            .WithMessage($"Pide como máximo {MaxDays} días a la vez.")
            .When(request => request.Date is null && request.From is not null && request.To is not null);
        RuleFor(request => request.LocationId)
            .Must(id => id is null || id.Value != Guid.Empty).WithMessage("Elige una sede válida.");
        RuleFor(request => request.ResourceIds)
            .Must(ids => ids is null || ids.All(id => id != Guid.Empty)).WithMessage("Elige recursos válidos.");
    }
}

public sealed record ListCalendarQuery(Guid TenantId, CalendarRequest Request) : IQuery<CalendarDto>;

/// <summary>
/// El calendario de la Agenda (plan 2026-11-21, Tarea 10), en una sola respuesta: por cada recurso del alcance, su horario, sus bloqueos,
/// sus citas y sus conteos. El alcance lo resuelve el servidor y lo aplican los repositorios (recursos, franjas, bloqueos, citas): sin
/// «ver todas las agendas» el miembro recibe sólo sus recursos, y pedir uno ajeno por id responde 403 antes de decir si existe.
/// </summary>
public sealed class ListCalendarHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    SchedulingFirstAccess firstAccess,
    ResourceScopeResolver scopeResolver,
    ILocationRepository locations,
    IServiceRepository services,
    IAppointmentReadRepository appointments,
    ISchedulingSetupReader setup,
    ISchedulingCustomerLookup customers,
    AvailabilityLoader loader,
    IValidator<CalendarRequest> validator)
    : IQueryHandler<ListCalendarQuery, CalendarDto>
{
    public async Task<CalendarDto> HandleAsync(ListCalendarQuery query, CancellationToken cancellationToken)
    {
        await SchedulingAuthorization.EnsureAsync(
            executionContext, tenantModules, query.TenantId, cancellationToken, SchedulingPermissions.AppointmentRead);
        var request = query.Request;
        await validator.ValidateAndThrowAsync(request, cancellationToken);
        await firstAccess.EnsureAsync(query.TenantId, cancellationToken);

        var scope = await scopeResolver.ResolveAsync(query.TenantId, cancellationToken);
        var requested = request.ResourceIds?.Distinct().ToArray() ?? [];
        if (requested.Any(id => !scope.Includes(id)))
        {
            throw SchedulingAuthorization.Denied();
        }

        if (request.LocationId is { } locationId
            && await locations.FindAsync(query.TenantId, new LocationId(locationId), cancellationToken) is null)
        {
            throw SchedulingNotFound.Location(locationId);
        }

        var from = request.Date ?? request.From!.Value;
        var to = request.Date ?? request.To!.Value;
        var loaded = await loader.LoadAsync(query.TenantId, scope, from, to, cancellationToken);

        // Un recurso pedido que no está entre los activos del alcance no existe para quien pregunta: 404, también si está archivado.
        var known = loaded.OfferedResources.Select(resource => resource.Id.Value).ToHashSet();
        if (requested.Where(id => !known.Contains(id)).Cast<Guid?>().FirstOrDefault() is { } missing)
        {
            throw SchedulingNotFound.Resource(missing);
        }

        var shown = loaded.OfferedResources
            .Where(resource => (requested.Length == 0 || requested.Contains(resource.Id.Value))
                && (request.LocationId is not { } location || resource.LocationId.Value == location))
            .ToArray();
        var shownIds = shown.Select(resource => resource.Id).ToArray();

        // Todas las sesiones del rango, activas o no: las activas ocupan y las publicadas abren ventanas, y la cita guarda su sesión aunque
        // ésta se haya quitado después. El motor ya ignora las inactivas.
        var sessions = await loader.SessionsAsync(query.TenantId, loaded, loaded.OfferedResourceIds, onlyActive: false, cancellationToken);
        var rows = await appointments.ListInRangeAsync(
            query.TenantId, scope, shownIds, loaded.RangeStart, loaded.RangeEnd, cancellationToken);
        var serviceList = await services.ListAsync(query.TenantId, cancellationToken);
        var serviceNames = serviceList.ToDictionary(service => service.Id, service => service.Name);
        var customerNames = await customers.FindNamesAsync(
            query.TenantId, [.. rows.Select(row => row.CustomerId).Distinct()], cancellationToken);
        var sessionsById = sessions.ToDictionary(session => session.Id);
        var free = FreeSlotCounter.Count(loaded, serviceList, sessions);
        var snapshot = await setup.ReadAsync(query.TenantId, cancellationToken);

        return new CalendarDto(
            SchedulingLayout.Of(snapshot.ActiveLocations, snapshot.ActiveResources),
            loaded.Calendar.TimeZone.Id,
            from,
            to,
            [.. shown.Select(resource =>
            {
                var drawn = rows.Where(row => row.ResourceId == resource.Id).ToArray();
                return new CalendarResourceDto(
                    resource.Id.Value,
                    resource.DisplayName,
                    resource.LocationId.Value,
                    drawn.Length,
                    free.GetValueOrDefault(resource.Id),
                    [.. OpenWindows.For(loaded.Blocks, sessions, resource.Id, loaded.FirstDay, loaded.LastDay, loaded.Holidays, loaded.Calendar.TimeZone)
                        .Select(window => new TimeWindowDto(window.Start, window.End))],
                    [.. loaded.Closures
                        .Where(closure => closure.StartsAt < loaded.RangeEnd
                            && closure.AppliesTo(resource.Id, resource.LocationId))
                        .Select(closure => new CalendarClosureDto(closure.Id.Value, closure.StartsAt, closure.EndsAt, closure.Reason))],
                    [.. drawn.Select(row =>
                    {
                        var session = sessionsById.GetValueOrDefault(row.SessionId);
                        return new CalendarAppointmentDto(
                            row.Id.Value,
                            row.StartsAt,
                            row.EndsAt,
                            row.Status.ToString(),
                            customerNames.GetValueOrDefault(row.CustomerId, AppointmentComposer.UnknownCustomer),
                            serviceNames.GetValueOrDefault(row.ServiceId, string.Empty),
                            row.Source.ToString(),
                            row.SessionId.Value,
                            session?.BookedCount ?? 0,
                            session?.Capacity ?? 0);
                    })]);
            })]);
    }
}
