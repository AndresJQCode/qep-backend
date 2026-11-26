using BuildingBlocks.Application;
using FluentValidation;
using Modules.Scheduling.Domain;

namespace Modules.Scheduling.Application;

public sealed record CancelAppointmentCommand(Guid TenantId, Guid AppointmentId, CancelAppointmentRequest? Request, string? IfMatch)
    : ICommand<AppointmentDto>;

/// <summary>
/// The cutoff of a client's own cancellation (spec 2026-11-21, «Citas»): through a channel the client cancels until
/// <c>client_cancel_cutoff_hours</c> before the start; the back office has no limit. Pure so it is tested without a database.
/// </summary>
public static class AppointmentCancellationRules
{
    public static bool ClientMayCancel(DateTimeOffset startsAt, DateTimeOffset now, int cutoffHours) =>
        startsAt - now >= TimeSpan.FromHours(cutoffHours);

    public static string TooLateMessage(int cutoffHours) =>
        $"Ya no puedes cancelar esta cita por tu cuenta: se puede hasta {cutoffHours} horas antes. Comunícate con nosotros.";

    /// <summary>
    /// The last instant a client may change the appointment on their own (D-15): moving it by chat has the same cutoff as cancelling it, so
    /// nobody dodges the deadline by moving the appointment instead of cancelling it.
    /// </summary>
    public static DateTimeOffset ChangeByChatUntil(DateTimeOffset startsAt, int cutoffHours) => startsAt - TimeSpan.FromHours(cutoffHours);

    public static string TooLateToMoveMessage(int cutoffHours) =>
        $"Ya no puedes cambiar la hora de esta cita por tu cuenta: se puede hasta {cutoffHours} horas antes. Comunícate con nosotros.";

    public const string NotMovableByChatMessage = "Esta cita sólo la puede mover el negocio.";
}

/// <summary>
/// Cancelar una cita: carga y autoriza (<c>If-Match</c>, alcance), valida y deja el resto al núcleo <see cref="AppointmentCancellation"/>, que
/// libera el cupo de su sesión en la misma transacción y bajo el mismo candado de recurso que la reserva. Un cliente por canal no cancela dentro
/// del plazo de las reglas; el back office sí.
/// </summary>
public sealed class CancelAppointmentHandler(
    AppointmentTransitions transitions,
    AppointmentCancellation cancellation,
    IValidator<CancelAppointmentRequest> validator)
    : ICommandHandler<CancelAppointmentCommand, AppointmentDto>
{
    public async Task<AppointmentDto> HandleAsync(CancelAppointmentCommand command, CancellationToken cancellationToken)
    {
        var (appointment, _) = await transitions.LoadAsync(command.TenantId, command.AppointmentId, command.IfMatch, cancellationToken);
        var request = command.Request ?? new CancelAppointmentRequest(null, null);
        await validator.ValidateAndThrowAsync(request, cancellationToken);

        var byClient = SchedulingEnums.TryParse<AppointmentSource>(request.Source, out var source) && source == AppointmentSource.Channel;
        var actor = await transitions.ActorAsync(command.TenantId, byClient, cancellationToken);

        await cancellation.CancelAsync(command.TenantId, appointment, request.Reason, byClient, actor, appointment.Version, cancellationToken);
        return await transitions.ComposeAsync(command.TenantId, appointment, cancellationToken);
    }
}
