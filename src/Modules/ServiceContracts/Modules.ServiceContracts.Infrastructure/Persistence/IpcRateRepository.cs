using Microsoft.EntityFrameworkCore;
using Modules.ServiceContracts.Application;
using Modules.ServiceContracts.Domain;

namespace Modules.ServiceContracts.Infrastructure.Persistence;

internal sealed class IpcRateRepository(ServiceContractsDbContext dbContext) : IIpcRateRepository
{
    public void Add(IpcRate rate) => dbContext.IpcRates.Add(rate);

    public Task<IpcRate?> FindAsync(Guid tenantId, int year, CancellationToken cancellationToken) =>
        dbContext.IpcRates.FirstOrDefaultAsync(rate => rate.TenantId == tenantId && rate.Year == year, cancellationToken);

    // El candado va en SQL porque EF no lo expresa. Con ToListAsync y sin componer nada encima, la sentencia llega tal cual. En
    // READ COMMITTED, la que esperó el candado devuelve la fila que confirmó quien lo tenía, no la de antes de esperar.
    public async Task<IpcRate?> FindForShareAsync(Guid tenantId, int year, CancellationToken cancellationToken)
    {
        RequireTransaction();
        var rows = await dbContext.IpcRates
            .FromSql($"SELECT * FROM billing.ipc_rates WHERE tenant_id = {tenantId} AND year = {year} FOR SHARE")
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        return rows.SingleOrDefault();
    }

    // Con tracking: quien llama no puede haber leído antes esta fila en el mismo contexto, o EF devolvería aquella copia.
    public async Task<IpcRate?> FindForUpdateAsync(Guid tenantId, int year, CancellationToken cancellationToken)
    {
        RequireTransaction();
        var rows = await dbContext.IpcRates
            .FromSql($"SELECT * FROM billing.ipc_rates WHERE tenant_id = {tenantId} AND year = {year} FOR UPDATE")
            .ToListAsync(cancellationToken);
        return rows.SingleOrDefault();
    }

    public async Task<IReadOnlyList<IpcRate>> ListAsync(Guid tenantId, CancellationToken cancellationToken) =>
        await dbContext.IpcRates
            .AsNoTracking()
            .Where(rate => rate.TenantId == tenantId)
            .OrderByDescending(rate => rate.Year)
            .ToListAsync(cancellationToken);

    // Sin transacción el candado de fila se suelta al terminar la sentencia y no protege nada.
    private void RequireTransaction()
    {
        if (dbContext.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "The IPC rate locks require the transaction of IServiceContractsUnitOfWork.BeginTransactionAsync.");
        }
    }
}
