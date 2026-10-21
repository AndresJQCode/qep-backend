namespace Modules.Messaging.Domain;

/// <summary>Asignación de un horario a una conexión: una fila sin comportamiento, escrita por el handler de asignación.</summary>
public sealed class ConnectionBusinessHours
{
    // EF Core materializa por acá; Create es el único punto de entrada.
    private ConnectionBusinessHours()
    {
    }

    public Guid ConnectionId { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ScheduleId { get; private set; }

    public DateTimeOffset AssignedAt { get; private set; }

    /// <summary>Asignar una conexión que ya tenía otro horario: la fila se reutiliza porque la conexión es su clave (D-H10).</summary>
    public void MoveTo(Guid scheduleId, DateTimeOffset now)
    {
        ScheduleId = scheduleId;
        AssignedAt = now;
    }

    public static ConnectionBusinessHours Create(Guid connectionId, Guid tenantId, Guid scheduleId, DateTimeOffset now) => new()
    {
        ConnectionId = connectionId,
        TenantId = tenantId,
        ScheduleId = scheduleId,
        AssignedAt = now,
    };
}
