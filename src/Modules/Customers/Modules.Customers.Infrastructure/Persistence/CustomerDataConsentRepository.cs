using Microsoft.EntityFrameworkCore;
using Modules.Customers.Application;
using Modules.Customers.Domain;

namespace Modules.Customers.Infrastructure.Persistence;

/// <summary>Spec 2026-10-26 D-5. Sólo lee y agrega: la tabla es prueba legal y nunca se edita ni se borra desde acá.</summary>
internal sealed class CustomerDataConsentRepository(CustomersDbContext dbContext) : ICustomerDataConsentRepository
{
    public async Task<IReadOnlyList<CustomerDataConsent>> ListAsync(
        Guid tenantId, CustomerId customerId, CancellationToken cancellationToken) =>
        await Ordered(tenantId, customerId).ToListAsync(cancellationToken);

    public Task<CustomerDataConsent?> FindLatestAsync(Guid tenantId, CustomerId customerId, CancellationToken cancellationToken) =>
        Ordered(tenantId, customerId).FirstOrDefaultAsync(cancellationToken);

    public Task<bool> ReplyRecordedAsync(Guid tenantId, Guid replyMessageId, CancellationToken cancellationToken) =>
        dbContext.DataConsents.AnyAsync(
            consent => consent.TenantId == tenantId && consent.ReplyMessageId == replyMessageId, cancellationToken);

    public async Task<Guid?> LockCustomerAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken)
    {
        // Sin transacción el lock se suelta al terminar la sentencia y no protege nada.
        if (dbContext.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("LockCustomerAsync requires the transaction of ICustomersUnitOfWork.BeginTransactionAsync.");
        }

        // Toma el cliente y después el que quedó; una fusión encadenada los toma al revés, y PostgreSQL corta el ciclo con un
        // deadlock (40P01). Es una carrera, no un error del servidor: 412 concurrency.conflict, como las sentencias de
        // CustomerMergeStore, que tampoco pasan por el unit of work. La transacción queda abortada y quien llama la libera.
        Guid? lockedId = null;
        await CustomersWriteRaces.RunAsync(async () => lockedId = await LockWithSurvivorAsync(tenantId, customerId, cancellationToken));
        return lockedId;
    }

    private async Task<Guid?> LockWithSurvivorAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken)
    {
        var customer = await LockAsync(tenantId, customerId, cancellationToken);
        if (customer?.MergedIntoId is not { } survivorId)
        {
            return customer?.Id;
        }

        // Un salto y no una cadena: la fusión reapunta a quienes apuntaban al fusionado (RepointMergedIntoAsync).
        return (await LockAsync(tenantId, survivorId, cancellationToken))?.Id;
    }

    public void Add(CustomerDataConsent consent) => dbContext.DataConsents.Add(consent);

    // Sin tracking: las filas nunca se modifican. Lo sirve IX_data_consents_tenant_customer_order.
    private IQueryable<CustomerDataConsent> Ordered(Guid tenantId, CustomerId customerId) =>
        dbContext.DataConsents
            .AsNoTracking()
            .Where(consent => consent.TenantId == tenantId && consent.CustomerId == customerId)
            .OrderByDescending(consent => consent.OccurredAt)
            .ThenByDescending(consent => consent.Sequence);

    // Sin componer (ni First ni Single encima): EF envolvería el FOR UPDATE en una subconsulta.
    private async Task<LockedCustomer?> LockAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken) =>
        (await dbContext.Database.SqlQuery<LockedCustomer>(
            $"""
            SELECT id AS "Id", merged_into_id AS "MergedIntoId" FROM customers.customers
            WHERE tenant_id = {tenantId} AND id = {customerId}
            FOR UPDATE
            """).ToListAsync(cancellationToken)).SingleOrDefault();

    private sealed record LockedCustomer(Guid Id, Guid? MergedIntoId);
}
