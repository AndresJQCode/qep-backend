using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Modules.Loans.Application;
using Modules.Loans.Domain;

namespace Modules.Loans.Infrastructure.Persistence;

internal sealed class LoansUnitOfWork(LoansDbContext dbContext) : ILoansUnitOfWork
{
    /// <summary>Único de cuotas (InitialLoans): un período de un préstamo se convierte en cuota una sola vez.</summary>
    private const string InstallmentPeriodIndex = "IX_installments_loan_period";

    // Borrar clientes (spec 2026-11-10, P1/P2): guardar un préstamo es escribir una referencia a customers.customers, que no
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

    // Clientes distintos y no vacíos de todo Loan nuevo o con CustomerId modificado, ordenados para tomar los candados
    // siempre en el mismo orden.
    private List<(Guid TenantId, Guid CustomerId)> PendingCustomerReferences()
    {
        var found = new HashSet<(Guid TenantId, Guid CustomerId)>();

        foreach (var entry in dbContext.ChangeTracker.Entries())
        {
            if (entry.Entity is not Loan
                || entry.State is not (EntityState.Added or EntityState.Modified))
            {
                continue;
            }

            var customerId = entry.Property(nameof(Loan.CustomerId));
            if (entry.State == EntityState.Modified && !customerId.IsModified)
            {
                continue;
            }

            var id = (Guid)customerId.CurrentValue!;
            if (id != Guid.Empty)
            {
                found.Add(((Guid)entry.Property(nameof(Loan.TenantId)).CurrentValue!, id));
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
                throw new LoansDomainException(
                    LoansErrorCodes.CustomerNotFound,
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
        // Traducido acá y no en Application, que no referencia EF Core.
        catch (DbUpdateConcurrencyException exception)
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict",
                "The loan record changed while the update was being committed.",
                exception);
        }
        // El worker pudo generar el período entre que se leyó el préstamo y se guardó: pierde el que llega segundo y sólo tiene que
        // reintentar, así que es el mismo conflicto de concurrencia y no un 500. Se reconoce por nombre de índice (precedente
        // TenancyUnitOfWork) y no por 23505, porque otra violación única sí sería un error.
        catch (DbUpdateException exception) when (UniqueViolation.Is(exception, InstallmentPeriodIndex))
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict",
                "The installment for this period was generated while the update was being committed.",
                exception);
        }
    }
}
