using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;
using Modules.Tenancy.Application;

namespace Modules.Messaging.Infrastructure.Persistence;

/// <summary>Spec 2026-10-21 §5: los horarios con tracking (la versión es el token de concurrencia) y, además, el puerto
/// <see cref="IBusinessHours"/>: asignación de la conexión → predeterminado del tenant → siempre abierto (D-H4), evaluado en el huso y a
/// la hora de <see cref="ITenantClock"/>.</summary>
internal sealed class BusinessHoursRepository(MessagingDbContext dbContext, ITenantClock tenantClock, IMessagingAuditRecorder audit) : IBusinessHoursRepository, IBusinessHours
{
    private static readonly StringComparer NameOrder = StringComparer.Create(CultureInfo.InvariantCulture, CompareOptions.IgnoreCase);

    public Task<BusinessHoursSchedule?> FindAsync(Guid tenantId, Guid id, CancellationToken cancellationToken) =>
        dbContext.BusinessHours.SingleOrDefaultAsync(schedule => schedule.TenantId == tenantId && schedule.Id == id, cancellationToken);

    public async Task<IReadOnlyList<BusinessHoursSchedule>> ListAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var rows = await dbContext.BusinessHours.Where(schedule => schedule.TenantId == tenantId).ToListAsync(cancellationToken);
        // El orden lo da .NET y no la collation de la base, que depende de cómo se creó el clúster (mismo criterio que los motivos).
        return rows.OrderBy(schedule => schedule.Name, NameOrder).ThenBy(schedule => schedule.Id).ToArray();
    }

    public Task<BusinessHoursSchedule?> FindDefaultAsync(Guid tenantId, CancellationToken cancellationToken) =>
        dbContext.BusinessHours.SingleOrDefaultAsync(schedule => schedule.TenantId == tenantId && schedule.IsDefault, cancellationToken);

    // La columna usa ToLower() sin argumentos: es la única forma que Npgsql traduce a lower(), la misma expresión de IX_business_hours_tenant_name
    // (ToLowerInvariant y Equals(OrdinalIgnoreCase) no se traducen). El lado del parámetro se evalúa en .NET, así que va invariante. Las reglas
    // de cultura (CA1304, CA1311) apuntan a .NET y acá la columna se compara en la base, como el índice; el pragma cubre sólo esa expresión.
    public Task<bool> NameExistsAsync(Guid tenantId, string name, CancellationToken cancellationToken)
    {
        var lowered = name.ToLowerInvariant();
#pragma warning disable CA1304, CA1311, CA1862
        return dbContext.BusinessHours.AnyAsync(
            schedule => schedule.TenantId == tenantId && schedule.Name.ToLower() == lowered, cancellationToken);
#pragma warning restore CA1304, CA1311, CA1862
    }

    public void Add(BusinessHoursSchedule schedule) => dbContext.BusinessHours.Add(schedule);

    public void Remove(BusinessHoursSchedule schedule) => dbContext.BusinessHours.Remove(schedule);

    public async Task<IReadOnlyList<ConnectionBusinessHours>> ListAssignmentsAsync(Guid tenantId, CancellationToken cancellationToken) =>
        await dbContext.ConnectionBusinessHours.Where(row => row.TenantId == tenantId).ToListAsync(cancellationToken);

    public void AddAssignment(ConnectionBusinessHours row) => dbContext.ConnectionBusinessHours.Add(row);

    public void RemoveAssignment(ConnectionBusinessHours row) => dbContext.ConnectionBusinessHours.Remove(row);

    public async Task SwapDefaultAsync(
        BusinessHoursSchedule? previousDefault, BusinessHoursSchedule target, DateTimeOffset now, Guid actorId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        // Como TryAutoAssignAsync, una transacción explícita dentro del repositorio. Dos guardados: el primero deja al anterior sin
        // predeterminado y el segundo pone el nuevo, así el índice parcial nunca ve dos a la vez sin importar el orden de los ids.
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        if (previousDefault is not null)
        {
            previousDefault.ClearDefault(now);
            await MessagingSaveChanges.SaveAsync(dbContext, cancellationToken);
        }

        target.MakeDefault(now);
        audit.RecordBusinessHours(target.TenantId, actorId, MessagingAuditActions.BusinessHoursDefaultSet, target.Id, now);
        await MessagingSaveChanges.SaveAsync(dbContext, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<BusinessHoursStatus> GetStatusAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        (await GetStatusesAsync(tenantId, [connectionId], cancellationToken))[connectionId];

    public async Task<IReadOnlyDictionary<Guid, BusinessHoursStatus>> GetStatusesAsync(
        Guid tenantId, IReadOnlyCollection<Guid> connectionIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connectionIds);
        var result = new Dictionary<Guid, BusinessHoursStatus>();
        if (connectionIds.Count == 0)
        {
            return result;
        }

        // Una carga de asignaciones y otra de horarios (a lo sumo unas decenas por tenant), sin tracking: sólo se leen.
        var calendar = await tenantClock.GetAsync(tenantId, cancellationToken);
        var assignments = await dbContext.ConnectionBusinessHours.AsNoTracking()
            .Where(row => row.TenantId == tenantId).ToDictionaryAsync(row => row.ConnectionId, row => row.ScheduleId, cancellationToken);
        var schedules = await dbContext.BusinessHours.AsNoTracking()
            .Where(schedule => schedule.TenantId == tenantId).ToDictionaryAsync(schedule => schedule.Id, cancellationToken);
        var fallback = schedules.Values.FirstOrDefault(schedule => schedule.IsDefault);

        foreach (var connectionId in connectionIds.Distinct())
        {
            var schedule = assignments.TryGetValue(connectionId, out var scheduleId) && schedules.TryGetValue(scheduleId, out var assigned)
                ? assigned
                : fallback;
            result[connectionId] = schedule is null
                ? new BusinessHoursStatus(true, null, null, null)
                : new BusinessHoursStatus(
                    schedule.IsOpenAt(calendar.UtcNow, calendar.TimeZone),
                    schedule.NextChangeAt(calendar.UtcNow, calendar.TimeZone),
                    schedule.Id,
                    schedule.Name);
        }

        return result;
    }
}
