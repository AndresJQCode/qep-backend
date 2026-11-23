using Modules.Scheduling.Domain;

namespace Modules.Scheduling.Application;

/// <summary>
/// Lo que la reserva necesita ya resuelto por quien llama. <paramref name="RequireClientChoice"/> exige que el recurso pedido sea uno que la
/// franja deja elegir al cliente (D-11, D-16): lo pide el agente; el back office reserva con el recurso que quiera.
/// </summary>
public sealed record BookingInput(
    Guid TenantId,
    Guid ServiceId,
    Guid CustomerId,
    DateTimeOffset StartsAt,
    ResourceId? ResourceId,
    AppointmentSource Source,
    Guid? ConversationId,
    string? Note,
    string? ClientRequestId,
    bool RequireClientChoice);

/// <summary><paramref name="Replayed"/> es verdadero cuando la llave de idempotencia ya tenía su cita y no se tomó otro cupo.</summary>
public sealed record BookingOutcome(Appointment Appointment, bool Replayed);

/// <summary>
/// El núcleo de la reserva (spec 2026-11-23 §6.2), sin autorización: lo usan <c>BookAppointmentHandler</c> (después de autorizar y acotar al
/// alcance) y <c>IChannelScheduling</c>. Va desde la primera búsqueda por <c>clientRequestId</c> hasta el commit, en este orden: repetición →
/// servicio → cliente → recurso → inicio ofrecido por las reglas → candado de recursos → repetición otra vez (la primera corrió antes del
/// candado) → relectura bajo el candado → cupo → cita, historial y auditoría. Sin recurso elegido, el «primero libre» se decide dentro del
/// candado, no al leer la disponibilidad: lo contrario deja a dos clientes con el mismo recurso.
/// </summary>
public sealed class AppointmentBooking(
    IServiceRepository services,
    IResourceRepository resources,
    ISchedulingCustomerLookup customers,
    AvailabilityLoader loader,
    ISessionRepository sessions,
    IAppointmentRepository appointments,
    ISchedulingAuditPublisher auditPublisher,
    ISchedulingUnitOfWork unitOfWork)
{
    public async Task<BookingOutcome> BookAsync(
        BookingInput input, ResourceScope scope, SchedulingActor actor, CancellationToken cancellationToken)
    {
        var tenantId = input.TenantId;
        var clientRequestId = string.IsNullOrWhiteSpace(input.ClientRequestId) ? null : input.ClientRequestId.Trim();
        if (clientRequestId is not null
            && await appointments.FindByClientRequestAsync(tenantId, clientRequestId, cancellationToken) is { } repeated)
        {
            return Replay(scope, input.CustomerId, repeated);
        }

        var service = (await services.FindManyAsync(tenantId, [new ServiceId(input.ServiceId)], cancellationToken)).SingleOrDefault();
        if (service is null || service.Archived)
        {
            SchedulingFieldErrors.Throw("serviceId", "El servicio no existe en esta organización o está archivado.");
        }

        var customer = await customers.FindAsync(tenantId, input.CustomerId, cancellationToken);
        if (customer is null || !customer.IsActive)
        {
            SchedulingFieldErrors.Throw("customerId", "El cliente no existe en esta organización o está inactivo.");
        }

        var requested = input.ResourceId;
        if (requested is { } chosen)
        {
            var resource = (await resources.FindManyAsync(tenantId, [chosen], cancellationToken)).SingleOrDefault();
            if (resource is null || resource.Archived)
            {
                SchedulingFieldErrors.Throw("resourceId", "El recurso no existe en esta organización o está archivado.");
            }
        }

        if (input.Source == AppointmentSource.Channel && !service.BookableByChannel)
        {
            throw new SchedulingDomainException(
                SchedulingErrorCodes.OutsideRules, "Ese servicio no se reserva por canales: sólo desde el back office.");
        }

        var startsAt = input.StartsAt.ToUniversalTime();
        var loaded = await loader.LoadForStartAsync(tenantId, scope, startsAt, cancellationToken);
        var rulesSessions = await loader.SessionsAsync(tenantId, loaded, loaded.OfferedResourceIds, onlyActive: false, cancellationToken);
        var candidates = AppointmentStarts.RulesCandidates(loaded, service, rulesSessions, startsAt, requested, _ => true);
        if (input.RequireClientChoice && requested is { } mustBeChoosable)
        {
            AppointmentStarts.EnsureClientChoice(loaded, service, rulesSessions, startsAt, mustBeChoosable);
        }

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        await appointments.LockResourcesAsync(tenantId, candidates, cancellationToken);

        // The first check ran before the lock: a request with the same key that was already inside has committed by now.
        if (clientRequestId is not null
            && await appointments.FindByClientRequestAsync(tenantId, clientRequestId, cancellationToken) is { } concurrent)
        {
            return Replay(scope, input.CustomerId, concurrent);
        }

        // Closures, blocks and archived resources are read again under the lock: a closure created while this request was computing its
        // candidates holds the same lock and has committed by now (its own appointment check ran before this booking existed).
        loaded = await loader.LoadForStartAsync(tenantId, scope, startsAt, cancellationToken);
        var fresh = await loader.SessionsAsync(tenantId, loaded, candidates, onlyActive: true, cancellationToken);
        var option = AppointmentStarts.PickFree(loaded, service, fresh, startsAt, candidates, preferred: null);

        var now = loaded.Calendar.UtcNow;
        var session = await AppointmentStarts.TakeSeatAsync(sessions, tenantId, option, service, startsAt, now, cancellationToken);
        var locationId = loaded.OfferedResources.Single(resource => resource.Id == option.ResourceId).LocationId;

        // A back-office booking is the secretary's word; a channel one waits for the tenant when the rules say so (spec D-5).
        var status = input.Source == AppointmentSource.BackOffice || loaded.Rules.ConfirmationMode == ConfirmationMode.Automatic
            ? AppointmentStatus.Confirmed
            : AppointmentStatus.Pending;

        var appointment = Appointment.Create(
            AppointmentId.New(),
            tenantId,
            locationId,
            option.ResourceId,
            service.Id,
            customer.Id,
            session.Id,
            option.BlockId,
            startsAt,
            startsAt.AddMinutes(service.DurationMinutes),
            status,
            input.Source,
            input.ConversationId,
            input.Note,
            actor.HistoryMemberId,
            now,
            clientRequestId,
            actor.ByChannel);
        appointments.Add(appointment);

        SchedulingAudit.Publish(
            auditPublisher, actor, tenantId, SchedulingAuditActions.AppointmentCreated,
            SchedulingAuditActions.Resources.Appointment, appointment.Id.Value,
            ["serviceId", "resourceId", "customerId", "startsAt", "status", "source"], now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new BookingOutcome(appointment, Replayed: false);
    }

    // A repeat answers with the appointment already booked, for whoever may see it: another member's agenda stays out of reach, and so does
    // another customer's appointment (the key identifies a booking, not who may read it).
    private static BookingOutcome Replay(ResourceScope scope, Guid customerId, Appointment appointment)
    {
        EnsureSameCustomer(appointment, customerId);
        return scope.Includes(appointment.ResourceId.Value)
            ? new BookingOutcome(appointment, Replayed: true)
            : throw SchedulingAuthorization.Denied();
    }

    /// <summary>
    /// A key already used for another customer's appointment is a conflict on the key and says nothing of the stored appointment: replaying it
    /// would describe somebody else's booking to this caller. The same customer with other arguments still gets the stored one, which is the
    /// truth to tell (D-14).
    /// </summary>
    internal static void EnsureSameCustomer(Appointment stored, Guid customerId)
    {
        if (stored.CustomerId != customerId)
        {
            SchedulingFieldErrors.Throw("clientRequestId", "Esa llave de idempotencia ya se usó para otra reserva.");
        }
    }
}
