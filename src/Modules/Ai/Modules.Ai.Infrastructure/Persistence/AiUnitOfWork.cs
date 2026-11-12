using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Modules.Ai.Application;
using Modules.Ai.Domain;
using Npgsql;

namespace Modules.Ai.Infrastructure.Persistence;

/// <summary>Copia de <c>AutomationsUnitOfWork</c>: cada 23505 se traduce por el nombre del índice, nunca sólo por SqlState (CLAUDE.md).
/// Nombre repetido → 409; la versión vieja y dos publicaciones con el mismo número → 412. Si el guardado toca un agente o sus conexiones,
/// la ocupación neutral de Mensajería (<see cref="IAgentConnectionSlots"/>) se sincroniza en la misma transacción, abierta acá: un bot que
/// ya ocupa la conexión vuelve como 409 con el ocupante y nada queda escrito.</summary>
internal sealed class AiUnitOfWork(AiDbContext dbContext, IAgentConnectionSlots slots) : IAiUnitOfWork
{
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
    }

    /// <summary>Los tenants de los agentes o conexiones que este guardado agrega, cambia o borra. Renombrar también cuenta: la fila de
    /// Mensajería guarda el nombre para el 409 y la bandeja.</summary>
    private List<Guid> TenantsWithConnectionChanges() =>
        [.. dbContext.ChangeTracker.Entries()
            .Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
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
