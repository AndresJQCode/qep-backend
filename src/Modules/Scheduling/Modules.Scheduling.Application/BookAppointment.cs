using BuildingBlocks.Application;
using FluentValidation;
using Modules.Scheduling.Domain;
using Modules.Tenancy.Application;

namespace Modules.Scheduling.Application;

public sealed record BookAppointmentCommand(Guid TenantId, BookAppointmentRequest Request) : ICommand<AppointmentDto>;

/// <summary>
/// Reservar una cita, en una sola transacción (spec 2026-11-21, «Citas»): autoriza y acota al alcance, y deja el resto al núcleo
/// <see cref="AppointmentBooking"/>, que recalcula que el inicio sea válido (el servidor manda sobre el <c>startsAt</c> del cliente), bloquea la
/// fila de los recursos candidatos con <c>SELECT … FOR UPDATE</c>, lee las sesiones <b>ya con el candado</b>, busca o abre la sesión, comprueba el
/// cupo y escribe la cita con su historial y su auditoría. Repetir un <c>clientRequestId</c> devuelve la misma cita sin tomar otro cupo.
/// Una cita <c>Pending</c> ya aparta su cupo; una de back office nace <c>Confirmed</c> y una de canal sigue <c>confirmationMode</c>.
/// </summary>
public sealed class BookAppointmentHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    SchedulingFirstAccess firstAccess,
    ResourceScopeResolver scopeResolver,
    ISchedulingMemberDirectory members,
    AppointmentBooking booking,
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

        // A back-office booking is signed by the member of the caller; one declared as a channel booking leaves nobody in the history.
        var member = source == AppointmentSource.BackOffice
            ? await members.FindBySubjectAsync(tenantId, executionContext.SubjectId, cancellationToken)
            : null;
        var outcome = await booking.BookAsync(
            new BookingInput(
                tenantId,
                request.ServiceId!.Value,
                request.CustomerId!.Value,
                request.StartsAt!.Value,
                requested,
                source,
                request.ConversationId,
                request.Note,
                request.ClientRequestId,
                RequireClientChoice: false),
            scope,
            SchedulingActor.Human(executionContext.SubjectId, member?.MemberId),
            cancellationToken);

        return await composer.ComposeAsync(tenantId, outcome.Appointment, cancellationToken);
    }
}
