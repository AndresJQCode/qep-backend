using Modules.Messaging.Domain;

namespace Modules.Messaging.Application;

/// <summary>Spec 2026-10-21 §5: los horarios de atención del tenant y sus asignaciones a conexiones, con tracking (la versión es el token
/// de concurrencia). Todo método recibe <c>tenantId</c> y filtra por él.</summary>
public interface IBusinessHoursRepository
{
    /// <summary>Con tracking.</summary>
    Task<BusinessHoursSchedule?> FindAsync(Guid tenantId, Guid id, CancellationToken cancellationToken);

    /// <summary>Por nombre, con tracking.</summary>
    Task<IReadOnlyList<BusinessHoursSchedule>> ListAsync(Guid tenantId, CancellationToken cancellationToken);

    Task<BusinessHoursSchedule?> FindDefaultAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>Compara con <c>lower(name)</c>, la misma expresión de <c>IX_business_hours_tenant_name</c>.</summary>
    Task<bool> NameExistsAsync(Guid tenantId, string name, CancellationToken cancellationToken);

    void Add(BusinessHoursSchedule schedule);

    void Remove(BusinessHoursSchedule schedule);

    Task<IReadOnlyList<ConnectionBusinessHours>> ListAssignmentsAsync(Guid tenantId, CancellationToken cancellationToken);

    void AddAssignment(ConnectionBusinessHours row);

    void RemoveAssignment(ConnectionBusinessHours row);
}

/// <summary>Si una conexión atiende ahora, cuándo cambia y con qué horario se decidió (<c>null</c> = siempre abierto, sin horario).</summary>
public sealed record BusinessHoursStatus(bool IsOpen, DateTimeOffset? NextChangeAt, Guid? ScheduleId, string? ScheduleName);

/// <summary>Spec 2026-10-21: el puerto que consumen el asistente y las pantallas para saber si se atiende ahora.</summary>
public interface IBusinessHours
{
    /// <summary>Asignación de la conexión → predeterminado del tenant → siempre abierto (D-H4).</summary>
    Task<BusinessHoursStatus> GetStatusAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);

    /// <summary>Lo mismo para varias conexiones con una sola carga de asignaciones y horarios.</summary>
    Task<IReadOnlyDictionary<Guid, BusinessHoursStatus>> GetStatusesAsync(
        Guid tenantId, IReadOnlyCollection<Guid> connectionIds, CancellationToken cancellationToken);
}
