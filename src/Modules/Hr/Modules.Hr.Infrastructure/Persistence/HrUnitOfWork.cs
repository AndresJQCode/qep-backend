using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Modules.Hr.Application;

namespace Modules.Hr.Infrastructure.Persistence;

internal sealed class HrUnitOfWork(HrDbContext dbContext) : IHrUnitOfWork
{
    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        // Traducido acá y no en Application, que no referencia EF Core. `employee.version` es el token de concurrencia de toda
        // escritura sobre un empleado (412), y las de configuración tienen el suyo.
        catch (DbUpdateConcurrencyException exception)
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict",
                "The hr record changed while the update was being committed.",
                exception);
        }
    }
}
