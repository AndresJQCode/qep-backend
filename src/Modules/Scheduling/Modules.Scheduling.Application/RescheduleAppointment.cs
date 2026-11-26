using BuildingBlocks.Application;
using FluentValidation;
using Modules.Scheduling.Domain;

namespace Modules.Scheduling.Application;

public sealed record RescheduleAppointmentCommand(Guid TenantId, Guid AppointmentId, RescheduleAppointmentRequest Request, string? IfMatch)
    : ICommand<AppointmentDto>;

/// <summary>
/// Cambiar la hora (y, si se pide, el recurso) de una cita que sigue abierta: carga y autoriza (<c>If-Match</c>, alcance), rechaza el cambio a
/// la misma hora y deja el resto al núcleo <see cref="AppointmentRescheduling"/>, que vuelve a validar todo como una reserva nueva.
/// </summary>
public sealed class RescheduleAppointmentHandler(
    AppointmentTransitions transitions,
    AppointmentRescheduling rescheduling,
    IValidator<RescheduleAppointmentRequest> validator)
    : ICommandHandler<RescheduleAppointmentCommand, AppointmentDto>
{
    public async Task<AppointmentDto> HandleAsync(RescheduleAppointmentCommand command, CancellationToken cancellationToken)
    {
        var (appointment, scope) = await transitions.LoadAsync(command.TenantId, command.AppointmentId, command.IfMatch, cancellationToken);
        await validator.ValidateAndThrowAsync(command.Request, cancellationToken);

        // A closed appointment answers invalid_transition before anything is computed for it (the core checks it again, it is cheap).
        AppointmentRescheduling.EnsureOpen(appointment);

        var tenantId = command.TenantId;
        ResourceId? requested = command.Request.ResourceId is { } requestedId ? new ResourceId(requestedId) : null;
        if (requested is { } outsider && !scope.Includes(outsider.Value))
        {
            throw SchedulingAuthorization.Denied();
        }

        var startsAt = command.Request.StartsAt!.Value.ToUniversalTime();
        if (startsAt == appointment.StartsAt && (requested is null || requested == appointment.ResourceId))
        {
            SchedulingFieldErrors.Throw("startsAt", "La cita ya tiene ese horario. Elige otro.");
        }

        var actor = await transitions.ActorAsync(tenantId, actsAsClient: false, cancellationToken);
        await rescheduling.RescheduleAsync(
            tenantId, appointment, scope, requested, startsAt, actor, byClient: false, requireClientChoice: false, appointment.Version, cancellationToken);
        return await transitions.ComposeAsync(tenantId, appointment, cancellationToken);
    }
}
