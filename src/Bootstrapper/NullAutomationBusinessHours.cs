using Modules.Automations.Application;

namespace Bootstrapper;

/// <summary>Spec 2026-10-21 D-H10: adaptador provisional de <see cref="IAutomationBusinessHours"/> hasta que la tarea 16 lo conecte con
/// los horarios de Mensajería. Ninguna conexión tiene horario: el bloque Horario sale con el aviso <c>business_hours.not_assigned</c>.</summary>
internal sealed class NullAutomationBusinessHours : IAutomationBusinessHours
{
    public Task<IReadOnlyDictionary<Guid, string?>> ScheduleNamesAsync(
        Guid tenantId, IReadOnlyCollection<Guid> connectionIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, string?>>(new Dictionary<Guid, string?>());
}
