using Modules.Scheduling.Domain;

namespace Modules.Scheduling.Application;

/// <summary>
/// Arma el <see cref="AppointmentDto"/>: resuelve en una lectura por lista el nombre del recurso, del servicio, del cliente y de quien hizo
/// cada paso del historial, para que la pantalla no consulte cuatro listas por una cita (regla BFF).
/// </summary>
public sealed class AppointmentComposer(
    IResourceRepository resources,
    IServiceRepository services,
    ISchedulingCustomerLookup customers,
    ISchedulingMemberDirectory members)
{
    // Un cliente que ya no está entre los datos del tenant no debería existir (una cita bloquea borrarlo); si pasa, la fila igual se dibuja.
    internal const string UnknownCustomer = "Cliente sin nombre";

    public async Task<AppointmentDto> ComposeAsync(Guid tenantId, Appointment appointment, CancellationToken cancellationToken)
    {
        var resource = (await resources.FindManyAsync(tenantId, [appointment.ResourceId], cancellationToken)).SingleOrDefault();
        var service = (await services.FindManyAsync(tenantId, [appointment.ServiceId], cancellationToken)).SingleOrDefault();
        var customer = await customers.FindAsync(tenantId, appointment.CustomerId, cancellationToken);

        var actorIds = appointment.Events.Where(entry => entry.ActorMemberId is not null).Select(entry => entry.ActorMemberId!.Value).Distinct().ToList();
        var actorNames = actorIds.Count == 0
            ? []
            : (await members.FindAsync(tenantId, actorIds, cancellationToken)).ToDictionary(member => member.MemberId, member => member.DisplayName);

        return new AppointmentDto(
            appointment.Id.Value,
            appointment.Status.ToString(),
            appointment.Source.ToString(),
            appointment.StartsAt,
            appointment.EndsAt,
            appointment.LocationId.Value,
            appointment.ResourceId.Value,
            resource?.DisplayName ?? string.Empty,
            appointment.ServiceId.Value,
            service?.Name ?? string.Empty,
            appointment.CustomerId,
            customer?.Name ?? UnknownCustomer,
            customer?.Phone,
            appointment.SessionId.Value,
            appointment.Note,
            appointment.CancelReason,
            appointment.ConversationId,
            appointment.ReminderSentAt,
            appointment.CreatedAt,
            appointment.Version,
            [.. appointment.Events
                .OrderBy(entry => entry.At)
                .Select(entry => new AppointmentEventDto(
                    entry.Action.ToString(),
                    entry.At,
                    entry.ActorMemberId is { } actor && actorNames.TryGetValue(actor, out var name) ? name : null,
                    entry.Detail,
                    entry.ByChannel))]);
    }
}
