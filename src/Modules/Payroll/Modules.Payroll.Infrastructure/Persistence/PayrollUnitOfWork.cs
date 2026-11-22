using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Modules.Payroll.Application;

namespace Modules.Payroll.Infrastructure.Persistence;

internal sealed class PayrollUnitOfWork(PayrollDbContext dbContext) : IPayrollUnitOfWork
{
    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        // Traducido acá y no en Application, que no referencia EF Core. La versión del período (y la de la configuración) es el
        // token de concurrencia de toda escritura (412). Las violaciones únicas llegan con las tablas, por nombre de índice.
        catch (DbUpdateConcurrencyException exception)
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict",
                "The payroll record changed while the update was being committed.",
                exception);
        }
    }
}
