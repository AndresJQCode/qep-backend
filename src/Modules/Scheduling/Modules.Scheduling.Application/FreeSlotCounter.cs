using Modules.Scheduling.Domain;

namespace Modules.Scheduling.Application;

/// <summary>
/// Cuántos inicios libres tiene cada recurso en un rango: lo que dicen <see cref="AvailabilityEngine"/> y las reglas del tenant, no un
/// cálculo aparte, así que el calendario, el escritorio y la pantalla de reservar nunca discrepan. Se corre el motor una vez por servicio
/// activo y se cuentan los inicios distintos por recurso: un inicio que le sirve a dos servicios es un solo hueco para quien mira la agenda.
/// </summary>
public static class FreeSlotCounter
{
    public static IReadOnlyDictionary<ResourceId, int> Count(
        LoadedAvailability loaded, IReadOnlyList<Service> services, IReadOnlyList<Session> sessions)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(sessions);

        var starts = new HashSet<(ResourceId Resource, DateTimeOffset Start)>();
        foreach (var service in services.Where(service => !service.Archived))
        {
            foreach (var slot in AvailabilityEngine.Compute(loaded.Query(service, sessions)))
            {
                foreach (var option in slot.Options)
                {
                    starts.Add((option.ResourceId, slot.StartsAt));
                }
            }
        }

        return starts.GroupBy(start => start.Resource).ToDictionary(group => group.Key, group => group.Count());
    }
}
