using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Modules.Loans.Application;

namespace Modules.Loans.Infrastructure.Persistence;

internal sealed class LoansUnitOfWork(LoansDbContext dbContext) : ILoansUnitOfWork
{
    /// <summary>Único de cuotas (InitialLoans): un período de un préstamo se convierte en cuota una sola vez.</summary>
    private const string InstallmentPeriodIndex = "IX_installments_loan_period";

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
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
