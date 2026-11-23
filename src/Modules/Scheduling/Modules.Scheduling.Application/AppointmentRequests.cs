namespace Modules.Scheduling.Application;

// Todo es anulable para que el validador devuelva el campo y no un 400 de binding.

/// <summary>Los parámetros de <c>GET availability</c>: las fechas son días del calendario del tenant, ambas inclusive.</summary>
public sealed record AvailabilityRequest(Guid? ServiceId, DateOnly? From, DateOnly? To, Guid? ResourceId);

/// <param name="Name">Obligatorio.</param>
/// <param name="Phone">Opcional: sin él no hay recordatorio por WhatsApp, pero la cita se puede agendar.</param>
public sealed record NewCustomerRequest(string? Name, string? Phone);

/// <param name="ResourceId">Con quién. Nulo deja que el servidor asigne el primer recurso libre de la franja, dentro de la transacción.</param>
/// <param name="StartsAt">Sólo es la <b>petición</b>: el servidor recalcula que sea un inicio válido y responde 422 <c>outside_rules</c> si no lo es.</param>
/// <param name="Source">El nombre del miembro de <c>AppointmentSource</c>: <c>BackOffice</c> (nulo) o <c>Channel</c>, una cita que pidió el cliente por un
/// canal y que sigue <c>confirmationMode</c>. Una cita de back office nace confirmada.</param>
/// <param name="ClientRequestId">La llave de idempotencia de quien llama (el agente, un reintento): repetirla devuelve la misma cita sin tomar otro cupo.</param>
/// <param name="ConversationId">La conversación de Mensajería de la que salió, sin llave foránea: sólo para enlazar a ella.</param>
public sealed record BookAppointmentRequest(
    Guid? ServiceId,
    Guid? ResourceId,
    DateTimeOffset? StartsAt,
    Guid? CustomerId,
    string? Source,
    string? Note,
    string? ClientRequestId,
    Guid? ConversationId);

/// <param name="Source"><c>Channel</c> es el cliente cancelando por un canal: no puede dentro de <c>clientCancelCutoffHours</c>. Nulo o
/// <c>BackOffice</c> no tiene ese tope.</param>
public sealed record CancelAppointmentRequest(string? Reason, string? Source);

/// <param name="ResourceId">Nulo conserva el recurso si sigue ofreciendo ese inicio y, si no, toma el primero libre de la misma sede.</param>
public sealed record RescheduleAppointmentRequest(Guid? ResourceId, DateTimeOffset? StartsAt);
