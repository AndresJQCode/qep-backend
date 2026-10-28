using Modules.Automations.Application;
using Modules.Messaging.Application;

namespace Bootstrapper;

/// <summary>Spec 2026-10-21 D-H10 y §5.3: el nombre del horario efectivo de cada conexión (la asignación o, si no tiene, el predeterminado del
/// tenant), por <see cref="IBusinessHours.GetStatusesAsync"/>. <c>null</c> = sin horario: el bloque Horario sale con
/// <c>business_hours.not_assigned</c>. El bloque Horario del motor pregunta <see cref="IsOpenAsync"/>.</summary>
internal sealed class AutomationBusinessHours(IBusinessHours businessHours) : IAutomationBusinessHours
{
    public async Task<IReadOnlyDictionary<Guid, string?>> ScheduleNamesAsync(
        Guid tenantId, IReadOnlyCollection<Guid> connectionIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connectionIds);
        if (connectionIds.Count == 0)
        {
            return new Dictionary<Guid, string?>();
        }

        var statuses = await businessHours.GetStatusesAsync(tenantId, connectionIds, cancellationToken);
        return statuses.ToDictionary(pair => pair.Key, pair => pair.Value.ScheduleName);
    }

    /// <summary>Asignación → predeterminado del tenant → siempre abierto, la misma cadena con que la bandeja pinta el horario.</summary>
    public async Task<bool> IsOpenAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        (await businessHours.GetStatusAsync(tenantId, connectionId, cancellationToken)).IsOpen;
}
