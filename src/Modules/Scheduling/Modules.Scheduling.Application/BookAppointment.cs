using BuildingBlocks.Application;
using FluentValidation;
using Modules.Scheduling.Domain;
using Modules.Tenancy.Application;

namespace Modules.Scheduling.Application;

public sealed record BookAppointmentCommand(Guid TenantId, BookAppointmentRequest Request) : ICommand<AppointmentDto>;

/// <summary>
/// Reservar una cita, en una sola transacción (spec 2026-11-21, «Citas»): autoriza y acota al alcance, recalcula que el inicio sea válido
/// (el servidor manda sobre el <c>startsAt</c> del cliente), bloquea la fila de los recursos candidatos con <c>SELECT … FOR UPDATE</c>, lee
/// las sesiones <b>ya con el candado</b>, busca o abre la sesión, comprueba el cupo y escribe la cita con su historial y su auditoría.
/// Sin recurso elegido, el «primero libre» se decide acá, dentro del candado, y no al leer la disponibilidad: lo contrario deja a dos
/// clientes con el mismo recurso. Repetir un <c>clientRequestId</c> devuelve la misma cita sin tomar otro cupo.
/// Una cita <c>Pending</c> ya aparta su cupo; una de back office nace <c>Confirmed</c> y una de canal sigue <c>confirmationMode</c>.
/// </summary>
public sealed class BookAppointmentHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    SchedulingFirstAccess firstAccess,
    ResourceScopeResolver scopeResolver,
    IServiceRepository services,
    IResourceRepository resources,
    ISchedulingCustomerLookup customers,
    ISchedulingMemberDirectory members,
    AvailabilityLoader loader,
    ISessionRepository sessions,
    IAppointmentRepository appointments,
    ISchedulingAuditPublisher auditPublisher,
    ISchedulingUnitOfWork unitOfWork,
    AppointmentComposer composer,
    IValidator<BookAppointmentRequest> validator)
    : ICommandHandler<BookAppointmentCommand, AppointmentDto>
{
    public async Task<AppointmentDto> HandleAsync(BookAppointmentCommand command, CancellationToken cancellationToken)
    {
        await SchedulingAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, cancellationToken,
            SchedulingPermissions.AppointmentRead, SchedulingPermissions.AppointmentManage);
        await validator.ValidateAndThrowAsync(command.Request, cancellationToken);
        await firstAccess.EnsureAsync(command.TenantId, cancellationToken);

        var tenantId = command.TenantId;
        var request = command.Request;
        var source = SchedulingEnums.TryParse<AppointmentSource>(request.Source, out var parsed) ? parsed : AppointmentSource.BackOffice;
        var scope = await scopeResolver.ResolveAsync(tenantId, cancellationToken);
        ResourceId? requested = request.ResourceId is { } requestedId ? new ResourceId(requestedId) : null;
        if (requested is { } outsider && !scope.Includes(outsider.Value))
        {
            throw SchedulingAuthorization.Denied();
        }

        var clientRequestId = string.IsNullOrWhiteSpace(request.ClientRequestId) ? null : request.ClientRequestId.Trim();
        if (clientRequestId is not null
            && await appointments.FindByClientRequestAsync(tenantId, clientRequestId, cancellationToken) is { } repeated)
        {
            return await ReplayAsync(tenantId, scope, repeated, cancellationToken);
        }

        var service = (await services.FindManyAsync(tenantId, [new ServiceId(request.ServiceId!.Value)], cancellationToken)).SingleOrDefault();
        if (service is null || service.Archived)
        {
            SchedulingFieldErrors.Throw("serviceId", "El servicio no existe en esta organización o está archivado.");
        }

        var customer = await customers.FindAsync(tenantId, request.CustomerId!.Value, cancellationToken);
        if (customer is null || !customer.IsActive)
        {
            SchedulingFieldErrors.Throw("customerId", "El cliente no existe en esta organización o está inactivo.");
        }

        if (requested is { } chosen)
        {
            var resource = (await resources.FindManyAsync(tenantId, [chosen], cancellationToken)).SingleOrDefault();
            if (resource is null || resource.Archived)
            {
                SchedulingFieldErrors.Throw("resourceId", "El recurso no existe en esta organización o está archivado.");
            }
        }

        if (source == AppointmentSource.Channel && !service.BookableByChannel)
        {
            throw new SchedulingDomainException(
                SchedulingErrorCodes.OutsideRules, "Ese servicio no se reserva por canales: sólo desde el back office.");
        }

        var startsAt = request.StartsAt!.Value.ToUniversalTime();
        var loaded = await loader.LoadForStartAsync(tenantId, scope, startsAt, cancellationToken);
        var rulesSessions = await loader.SessionsAsync(tenantId, loaded, loaded.OfferedResourceIds, onlyActive: false, cancellationToken);
        var candidates = AppointmentStarts.RulesCandidates(loaded, service, rulesSessions, startsAt, requested, _ => true);

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        await appointments.LockResourcesAsync(tenantId, candidates, cancellationToken);

        // The first check ran before the lock: a request with the same key that was already inside has committed by now.
        if (clientRequestId is not null
            && await appointments.FindByClientRequestAsync(tenantId, clientRequestId, cancellationToken) is { } concurrent)
        {
            return await ReplayAsync(tenantId, scope, concurrent, cancellationToken);
        }

        var fresh = await loader.SessionsAsync(tenantId, loaded, candidates, onlyActive: true, cancellationToken);
        var option = AppointmentStarts.PickFree(loaded, service, fresh, startsAt, candidates, preferred: null);

        var now = loaded.Calendar.UtcNow;
        var session = await AppointmentStarts.TakeSeatAsync(sessions, tenantId, option, service, startsAt, now, cancellationToken);
        var locationId = loaded.OfferedResources.Single(resource => resource.Id == option.ResourceId).LocationId;

        // A back-office booking is the secretary's word; a channel one waits for the tenant when the rules say so (spec D-5).
        var status = source == AppointmentSource.BackOffice || loaded.Rules.ConfirmationMode == ConfirmationMode.Automatic
            ? AppointmentStatus.Confirmed
            : AppointmentStatus.Pending;
        var actor = source == AppointmentSource.BackOffice
            ? await members.FindBySubjectAsync(tenantId, executionContext.SubjectId, cancellationToken)
            : null;

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
            source,
            request.ConversationId,
            request.Note,
            actor?.MemberId,
            now,
            clientRequestId);
        appointments.Add(appointment);

        SchedulingAudit.Publish(
            auditPublisher, executionContext, tenantId, SchedulingAuditActions.AppointmentCreated,
            SchedulingAuditActions.Resources.Appointment, appointment.Id.Value,
            ["serviceId", "resourceId", "customerId", "startsAt", "status", "source"], now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return await composer.ComposeAsync(tenantId, appointment, cancellationToken);
    }

    // A repeat answers with the appointment already booked, for whoever may see it: another member's agenda stays out of reach.
    private async Task<AppointmentDto> ReplayAsync(Guid tenantId, ResourceScope scope, Appointment appointment, CancellationToken cancellationToken)
    {
        if (!scope.Includes(appointment.ResourceId.Value))
        {
            throw SchedulingAuthorization.Denied();
        }

        return await composer.ComposeAsync(tenantId, appointment, cancellationToken);
    }
}
