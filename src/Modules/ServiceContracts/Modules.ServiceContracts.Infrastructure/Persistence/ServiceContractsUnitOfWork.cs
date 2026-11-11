using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Modules.ServiceContracts.Application;
using Modules.ServiceContracts.Domain;
using Modules.ServiceContracts.Infrastructure.Reminders;

namespace Modules.ServiceContracts.Infrastructure.Persistence;

internal sealed class ServiceContractsUnitOfWork(ServiceContractsDbContext dbContext) : IServiceContractsUnitOfWork
{
    /// <summary>PK de customer_profiles (InitialBilling): el primer PUT del perfil y el primer servicio de un cliente sin perfil.</summary>
    private const string ProfilePrimaryKey = "PK_customer_profiles";

    /// <summary>PK de settings (InitialBilling): el primer PUT de los ajustes del tenant.</summary>
    private const string SettingsPrimaryKey = "PK_settings";

    public void Clear() => dbContext.ChangeTracker.Clear();

    // Borrar clientes (spec 2026-11-10, P1/P2): guardar un servicio es escribir una referencia a customers.customers, que no
    // tiene FK. Para que el borrado en bloque no deje una referencia huérfana, el escritor toma el candado compartido del
    // cliente y, ya con él, comprueba que la fila exista, todo dentro de la transacción que guarda. Si el borrado ganó la
    // carrera, el candado espera a su commit y la comprobación ve el cliente ya borrado.
    private async Task SaveHoldingCustomerReferencesAsync(CancellationToken cancellationToken)
    {
        var references = PendingCustomerReferences();
        if (references.Count == 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return;
        }

        // Con transacción abierta el candado queda en ella. Sin ella, el candado de transacción se soltaría al terminar su
        // propia sentencia: se abre una aquí (este UoW no la abría; SaveChanges la abría sólo por su cuenta).
        if (dbContext.Database.CurrentTransaction is not null)
        {
            await HoldCustomerReferencesAsync(references, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            return;
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await HoldCustomerReferencesAsync(references, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    // Clientes distintos y no vacíos de todo ServiceContract nuevo o con CustomerId modificado, ordenados para tomar los candados
    // siempre en el mismo orden.
    private List<(Guid TenantId, Guid CustomerId)> PendingCustomerReferences()
    {
        var found = new HashSet<(Guid TenantId, Guid CustomerId)>();

        foreach (var entry in dbContext.ChangeTracker.Entries())
        {
            if (entry.Entity is not ServiceContract
                || entry.State is not (EntityState.Added or EntityState.Modified))
            {
                continue;
            }

            var customerId = entry.Property(nameof(ServiceContract.CustomerId));
            if (entry.State == EntityState.Modified && !customerId.IsModified)
            {
                continue;
            }

            var id = (Guid)customerId.CurrentValue!;
            if (id != Guid.Empty)
            {
                found.Add(((Guid)entry.Property(nameof(ServiceContract.TenantId)).CurrentValue!, id));
            }
        }

        return found.OrderBy(pair => pair.TenantId).ThenBy(pair => pair.CustomerId).ToList();
    }

    private async Task HoldCustomerReferencesAsync(
        List<(Guid TenantId, Guid CustomerId)> references, CancellationToken cancellationToken)
    {
        foreach (var (tenantId, customerId) in references)
        {
            // Primero el candado y después la lectura (P1): al revés, se vería un cliente a punto de desaparecer.
            await dbContext.Database.ExecuteSqlAsync(
                CustomerReferenceLock.Shared(tenantId, customerId), cancellationToken);

            var exists = await dbContext.Database
                .SqlQuery<bool>(CustomerReferenceLock.Exists(tenantId, customerId))
                .SingleAsync(cancellationToken);
            if (!exists)
            {
                throw new ServiceContractsDomainException(
                    ServiceContractsErrorCodes.CustomerNotFound,
                    $"Customer '{customerId}' was not found.");
            }
        }
    }

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await SaveHoldingCustomerReferencesAsync(cancellationToken);
        }
        // Traducido acá y no en Application, que no referencia EF Core. Las violaciones de los
        // índices únicos (IX_charges_service_due, IX_customer_messages_...) no se traducen: son la
        // deduplicación del worker, y quien la usa decide qué hacer al perderla.
        catch (DbUpdateConcurrencyException exception)
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict",
                "The billing record changed while the update was being committed.",
                exception);
        }
        // Dos primeras escrituras a la vez (ambas vieron «no hay fila» y las dos insertan la misma clave): perdió la segunda. Es un
        // 409 reintentable —el reintento ya encuentra la fila y la actualiza—, no un 500. Se reconoce por nombre de PK
        // (precedente TenancyUnitOfWork) y no por 23505: los índices del worker son deduplicación que decide quien los usa.
        catch (DbUpdateException exception) when (UniqueViolation.Is(exception, ProfilePrimaryKey, SettingsPrimaryKey))
        {
            throw new ResourceConflictException(
                "billing.concurrent_first_write",
                "Another request created the same record first. Retry the request.",
                exception);
        }
    }
}
