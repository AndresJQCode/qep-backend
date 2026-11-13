using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Modules.Ai.Application;
using Modules.Ai.Domain;
using Npgsql;

namespace Modules.Ai.Infrastructure.Persistence;

/// <summary>Cada 23505 se traduce por el nombre del índice, nunca sólo por SqlState (CLAUDE.md). Nombre repetido → 409; la versión vieja,
/// dos publicaciones con el mismo número y una carrera al sincronizar la ocupación → 412. Si el guardado toca un agente o sus conexiones,
/// la ocupación neutral de Mensajería (<see cref="IAgentConnectionSlots"/>) se sincroniza en la misma transacción: un bot que ya ocupa la
/// conexión vuelve como 409 con el ocupante y nada queda escrito.
/// <para>A diferencia de <c>AutomationsUnitOfWork</c>, sólo confirma la transacción que abrió él: Ai no expone
/// <c>BeginTransactionAsync</c>, así que no hay una del llamador que confirmar. Si algún día la expone, tiene que decidir si guardar
/// confirma la del llamador, como hace Automations.</para></summary>
internal sealed class AiUnitOfWork(AiDbContext dbContext, IAgentConnectionSlots slots) : IAiUnitOfWork
{
    /// <summary>Las propiedades de <see cref="Agent"/> que mueven la ocupación: el nombre que guarda la fila de Mensajería, el estado y la
    /// versión publicada que deciden si ocupa (D-A10).</summary>
    private static readonly HashSet<string> SlotProperties = [nameof(Agent.Name), nameof(Agent.Status), nameof(Agent.PublishedVersionId)];

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        var tenants = TenantsWithConnectionChanges();
        await using var owned = tenants.Count > 0 && dbContext.Database.CurrentTransaction is null
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            foreach (var tenantId in tenants)
            {
                var taken = await slots.SyncAgentsAsync(tenantId, cancellationToken);
                if (taken.Count > 0)
                {
                    // Sin confirmar: al disponerse, la transacción deshace el agente y la ocupación juntos.
                    throw new AgentConnectionTakenException(taken[0]);
                }
            }

            if (owned is not null)
            {
                await owned.CommitAsync(cancellationToken);
            }
        }
        // Antes que la rama de DbUpdateException: DbUpdateConcurrencyException hereda de ella.
        catch (DbUpdateConcurrencyException exception)
        {
            throw Concurrency(exception);
        }
        catch (DbUpdateException exception) when (IsUnique(exception, AiDbContext.AgentNameIndex))
        {
            throw new ResourceConflictException(AiErrorCodes.AgentNameTaken, "Ya existe un agente con ese nombre.", exception);
        }
        catch (DbUpdateException exception) when (IsUnique(exception, AiDbContext.VersionNumberIndex))
        {
            throw Concurrency(exception);
        }
        // Después de las ramas de DbUpdateException: el ExecuteSqlAsync de la sincronización lanza la PostgresException sin envolver. Dos
        // guardados que igual se cruzan en connection_bots son una carrera, no un error del servidor. Precedente: CustomersWriteRaces.
        catch (Exception exception) when (IsWriteRace(exception))
        {
            throw Concurrency(exception);
        }
    }

    /// <summary>Un deadlock (<c>40P01</c>) o una falla de serialización (<c>40001</c>) en cualquier punto de la cadena de excepciones.
    /// Réplica de <c>CustomersWriteRaces.IsRace</c>: los módulos no se ven entre sí.</summary>
    internal static bool IsWriteRace(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException { SqlState: PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure })
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Los tenants de los agentes o conexiones que este guardado agrega, borra o cambia en algo que mueve la ocupación. Renombrar
    /// cuenta: la fila de Mensajería guarda el nombre para el 409 y la bandeja. Guardar el borrador no cuenta: el editor guarda mientras se
    /// escribe, y reescribir todas las filas del tenant en cada tecla sólo abre la puerta a que dos guardados se crucen.</summary>
    private List<Guid> TenantsWithConnectionChanges() =>
        [.. dbContext.ChangeTracker.Entries()
            .Where(entry => entry.State is EntityState.Added or EntityState.Deleted
                || (entry.State is EntityState.Modified && (entry.Entity is not Agent
                    || entry.Properties.Any(property => property.IsModified && SlotProperties.Contains(property.Metadata.Name)))))
            .Select(entry => entry.Entity switch
            {
                Agent agent => (Guid?)agent.TenantId,
                AgentConnection connection => connection.TenantId,
                _ => null,
            })
            .OfType<Guid>()
            .Distinct()];

    private static RequestConcurrencyException Concurrency(Exception inner) =>
        new("concurrency.conflict", "The agent changed while the operation was being committed.", inner);

    private static bool IsUnique(DbUpdateException exception, string indexName) =>
        exception.InnerException is PostgresException postgres &&
        postgres.SqlState == PostgresErrorCodes.UniqueViolation &&
        string.Equals(postgres.ConstraintName, indexName, StringComparison.Ordinal);
}
