using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Modules.Audit.Domain;
using Modules.Coordinadora.Application;
using Modules.Coordinadora.Domain;
using Npgsql;

namespace Modules.Coordinadora.Infrastructure.Persistence;

/// <summary>Con tracking: el llamador modifica y guarda con <c>ICoordinadoraUnitOfWork</c>. Todo filtra por <c>tenant_id</c>.</summary>
internal sealed class CoordinadoraSettingsRepository(CoordinadoraDbContext dbContext) : ICoordinadoraSettingsRepository
{
    public async Task<CoordinadoraSettings?> FindOrCreateAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        // Spec 2026-11-21 §5.2: la fila nace vacía al primer acceso. ON CONFLICT DO NOTHING, no «buscar y agregar»: dos primeros accesos
        // a la vez no chocan, y los dos leen la misma fila. Las columnas que no se nombran toman su default de la tabla.
        await dbContext.Database.ExecuteSqlAsync(
            $"INSERT INTO coordinadora.settings (connection_id, tenant_id) VALUES ({connectionId}, {tenantId}) ON CONFLICT (connection_id) DO NOTHING",
            cancellationToken);
        return await FindAsync(tenantId, connectionId, cancellationToken);
    }

    // Una fila con ese connection_id y otro tenant no es de este tenant: null (el llamador responde 403, nunca la crea ni la lee).
    public Task<CoordinadoraSettings?> FindAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        dbContext.Settings.SingleOrDefaultAsync(
            settings => settings.ConnectionId == connectionId && settings.TenantId == tenantId, cancellationToken);
}

/// <summary>Con tracking. Todo filtra por <c>tenant_id</c>.</summary>
internal sealed class CoordinadoraShipmentRepository(CoordinadoraDbContext dbContext) : ICoordinadoraShipmentRepository
{
    public async Task<IReadOnlyList<CoordinadoraShipment>> ListByOrderAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken) =>
        await dbContext.Shipments
            .Where(shipment => shipment.TenantId == tenantId && shipment.OrderId == orderId)
            .OrderBy(shipment => shipment.CreatedAt)
            .ThenBy(shipment => shipment.Id)
            .ToListAsync(cancellationToken);

    public void Add(CoordinadoraShipment shipment) => dbContext.Shipments.Add(shipment);
}

// Camino de auditoría atómica (ADR 0019), copia de SiigoAuditRecorder: la entrada se acumula en CoordinadoraDbContext y commitea o
// revierte con el cambio. audit.entries es del módulo Audit; acá se proyecta ExcludeFromMigrations.
internal sealed class CoordinadoraAuditRecorder(CoordinadoraDbContext dbContext) : ICoordinadoraAuditRecorder
{
    private const string Source = "coordinadora";
    private const string Outcome = "success";

    public void Record(
        Guid tenantId,
        Guid actorId,
        AuditActorType actorType,
        string action,
        string resourceType,
        string resourceId,
        string changedFieldsJson,
        DateTimeOffset occurredAt) =>
        dbContext.AuditEntries.Add(AuditEntry.Create(
            tenantId, actorId, actorType, action, resourceType, resourceId, Outcome, changedFieldsJson, Source, occurredAt));
}

/// <summary>
/// Guarda y traduce las violaciones de la base a los códigos del contrato. Se discrimina por el <b>nombre</b> del índice y no sólo por el
/// SqlState: 23505 dice que se violó algún índice único, no cuál. Traducido acá y no en Application, que no referencia Npgsql
/// (<c>CoordinadoraLayerTests</c>).
/// </summary>
internal sealed class CoordinadoraUnitOfWork(CoordinadoraDbContext dbContext) : ICoordinadoraUnitOfWork
{
    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (Translate(exception) is { } translated)
        {
            throw translated;
        }
    }

    public async Task<ICoordinadoraTransaction> BeginTransactionAsync(CancellationToken cancellationToken) =>
        new CoordinadoraTransaction(await dbContext.Database.BeginTransactionAsync(cancellationToken));

    public Task LockOrderAsync(Guid orderId, CancellationToken cancellationToken)
    {
        // Fuera de una transacción el candado se soltaría al terminar la sentencia y no serializaría nada.
        if (dbContext.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("LockOrderAsync requires an open transaction (BeginTransactionAsync).");
        }

        var key = CoordinadoraShipmentLock.Key(orderId);
        return dbContext.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock({CoordinadoraShipmentLock.Namespace}, hashtext({key}))", cancellationToken);
    }

    /// <summary>La traducción sola, para probarla sin base. <c>null</c>: no es nuestra y sube tal cual.</summary>
    internal static Exception? Translate(Exception exception) => exception switch
    {
        // Primero: DbUpdateConcurrencyException hereda de DbUpdateException.
        DbUpdateConcurrencyException => new RequestConcurrencyException(
            "concurrency.conflict", "The resource changed while the update was being committed.", exception),
        DbUpdateException update when IsUniqueViolation(update, CoordinadoraDbContext.ShipmentsTenantOrderLiveIndex) => new ResourceConflictException(
            CoordinadoraErrorCodes.ShipmentAlreadyExists, "The order already has a current Coordinadora guide.", exception),
        _ => null,
    };

    private static bool IsUniqueViolation(DbUpdateException exception, string constraintName) =>
        exception.InnerException is PostgresException postgres
        && postgres.SqlState == PostgresErrorCodes.UniqueViolation
        && string.Equals(postgres.ConstraintName, constraintName, StringComparison.Ordinal);

    private sealed class CoordinadoraTransaction(IDbContextTransaction transaction) : ICoordinadoraTransaction
    {
        public Task CommitAsync(CancellationToken cancellationToken) => transaction.CommitAsync(cancellationToken);

        // Disponer sin commit hace rollback, y el rollback es lo que suelta los candados.
        public ValueTask DisposeAsync() => transaction.DisposeAsync();
    }
}
