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
    // tiene FK. CustomerReferenceWrites toma el candado compartido de cada cliente y, ya con él, comprueba que la fila exista,
    // todo dentro de la transacción que guarda: si el borrado ganó la carrera, el candado espera a su commit y la comprobación
    // ve el cliente ya borrado.
    private static readonly CustomerReferenceSource LoanCustomer =
        new(typeof(Loan), nameof(Loan.CustomerId), LoansErrorCodes.CustomerNotFound);

    private Task<int> SaveHoldingCustomerReferencesAsync(CancellationToken cancellationToken) =>
        CustomerReferenceWrites.SaveHoldingAsync(
            dbContext,
            CustomerReferenceWrites.Collect(dbContext.ChangeTracker, LoanCustomer),
            reference => new LoansDomainException(reference.NotFoundCode, $"Customer '{reference.CustomerId}' was not found."),
            cancellationToken);

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
