using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace BuildingBlocks.Infrastructure;

/// <summary>
/// Una entidad que guarda una referencia suelta a <c>customers.customers</c> (sin FK). <paramref name="Priority"/> decide qué
/// <paramref name="NotFoundCode"/> sale cuando el mismo cliente aparece en dos fuentes del mismo guardado: gana la de prioridad
/// más alta. <paramref name="CustomerProperty"/> y <paramref name="TenantProperty"/> son nombres de propiedad del modelo de EF.
/// </summary>
public sealed record CustomerReferenceSource(
    Type Entity,
    string CustomerProperty,
    string NotFoundCode,
    int Priority = 0,
    string TenantProperty = "TenantId");

/// <summary>Un cliente al que el guardado pendiente escribe una referencia, con el código de error si ya no existe.</summary>
public sealed record CustomerReferenceToHold(Guid TenantId, Guid CustomerId, string NotFoundCode);

/// <summary>
/// El protocolo de <see cref="CustomerReferenceLock"/> del lado del escritor, una sola vez para todos los módulos (borrar
/// clientes, spec 2026-11-10, P1/P2): candado compartido y después <see cref="CustomerReferenceLock.Exists"/>, en sentencias
/// aparte, dentro de una transacción READ COMMITTED que dura hasta el commit de lo que se guarda. Si el borrado ganó la carrera,
/// el candado espera su commit y la comprobación ve el cliente ya borrado.
/// </summary>
public static class CustomerReferenceWrites
{
    /// <summary>
    /// Toma el candado compartido del cliente y devuelve si existe. Exige una transacción abierta (sin ella el candado se suelta
    /// al terminar su propia sentencia y no protege nada) y en READ COMMITTED: en REPEATABLE READ o SERIALIZABLE la foto se toma
    /// en la primera sentencia, antes de esperar el candado, y <c>Exists</c> seguiría viendo al cliente que el borrado se llevó.
    /// </summary>
    public static async Task<bool> HoldAsync(
        DatabaseFacade database, Guid tenantId, Guid customerId, CancellationToken cancellationToken)
    {
        var transaction = database.CurrentTransaction
            ?? throw new InvalidOperationException("The customer reference lock needs an open transaction.");
        var isolation = transaction.GetDbTransaction().IsolationLevel;
        if (isolation is not (IsolationLevel.ReadCommitted or IsolationLevel.Unspecified))
        {
            throw new InvalidOperationException(
                $"The customer reference lock needs a READ COMMITTED transaction; this one is {isolation}.");
        }

        await database.ExecuteSqlAsync(CustomerReferenceLock.Shared(tenantId, customerId), cancellationToken);
        return await database.SqlQuery<bool>(CustomerReferenceLock.Exists(tenantId, customerId)).SingleAsync(cancellationToken);
    }

    /// <summary>
    /// Los clientes distintos y no vacíos de toda entidad de <paramref name="sources"/> nueva o con el cliente modificado,
    /// ordenados por tenant y cliente para tomar los candados siempre en el mismo orden.
    /// </summary>
    public static IReadOnlyList<CustomerReferenceToHold> Collect(ChangeTracker changeTracker, params CustomerReferenceSource[] sources)
    {
        var found = new Dictionary<(Guid TenantId, Guid CustomerId), CustomerReferenceSource>();

        foreach (var entry in changeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified))
            {
                continue;
            }

            var source = Array.Find(sources, candidate => candidate.Entity.IsInstanceOfType(entry.Entity));
            if (source is null)
            {
                continue;
            }

            var customer = entry.Property(source.CustomerProperty);
            if (entry.State == EntityState.Modified && !customer.IsModified)
            {
                continue;
            }

            // Guid? o Guid: sin cliente (null o Guid.Empty, como el pedido manual) no se referencia a nadie.
            if (customer.CurrentValue is not Guid customerId || customerId == Guid.Empty)
            {
                continue;
            }

            var key = ((Guid)entry.Property(source.TenantProperty).CurrentValue!, customerId);
            if (!found.TryGetValue(key, out var existing) || source.Priority > existing.Priority)
            {
                found[key] = source;
            }
        }

        return found
            .OrderBy(pair => pair.Key.TenantId)
            .ThenBy(pair => pair.Key.CustomerId)
            .Select(pair => new CustomerReferenceToHold(pair.Key.TenantId, pair.Key.CustomerId, pair.Value.NotFoundCode))
            .ToList();
    }

    /// <summary>
    /// Guarda <paramref name="dbContext"/> sosteniendo el candado de cada referencia hasta el commit. Con transacción abierta los
    /// candados quedan en ella; sin ella se abre una aquí, porque la que abriría <c>SaveChanges</c> por su cuenta empieza después
    /// de los candados. Si un cliente ya no existe, lanza lo que devuelva <paramref name="notFound"/> y no guarda nada.
    /// </summary>
    public static async Task<int> SaveHoldingAsync(
        DbContext dbContext,
        IReadOnlyList<CustomerReferenceToHold> references,
        Func<CustomerReferenceToHold, Exception> notFound,
        CancellationToken cancellationToken)
    {
        if (references.Count == 0)
        {
            return await dbContext.SaveChangesAsync(cancellationToken);
        }

        if (dbContext.Database.CurrentTransaction is not null)
        {
            await HoldAllAsync(dbContext.Database, references, notFound, cancellationToken);
            return await dbContext.SaveChangesAsync(cancellationToken);
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await HoldAllAsync(dbContext.Database, references, notFound, cancellationToken);
        var saved = await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return saved;
    }

    private static async Task HoldAllAsync(
        DatabaseFacade database,
        IReadOnlyList<CustomerReferenceToHold> references,
        Func<CustomerReferenceToHold, Exception> notFound,
        CancellationToken cancellationToken)
    {
        foreach (var reference in references)
        {
            if (!await HoldAsync(database, reference.TenantId, reference.CustomerId, cancellationToken))
            {
                throw notFound(reference);
            }
        }
    }
}
