using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Modules.Billing.Application;

namespace Modules.Billing.Infrastructure.Persistence;

internal sealed class BillingUnitOfWork(BillingDbContext dbContext) : IBillingUnitOfWork
{
    public void Clear() => dbContext.ChangeTracker.Clear();

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
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
    }
}
