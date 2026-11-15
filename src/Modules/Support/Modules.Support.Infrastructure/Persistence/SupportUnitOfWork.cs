using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Modules.Support.Application;
using Modules.Support.Domain;

namespace Modules.Support.Infrastructure.Persistence;

internal sealed class SupportUnitOfWork(SupportDbContext dbContext) : ISupportUnitOfWork
{
    // Borrar clientes (spec 2026-11-10, P1/P2): guardar un ticket con cliente es escribir una referencia a customers.customers, que
    // no tiene FK. CustomerReferenceWrites toma el candado compartido de cada cliente y, ya con él, comprueba que la fila exista,
    // todo dentro de la transacción que guarda: si el borrado ganó la carrera, el candado espera a su commit y la comprobación ve
    // el cliente ya borrado (Review Focus 5).
    private static readonly CustomerReferenceSource TicketCustomer =
        new(typeof(SupportTicket), nameof(SupportTicket.CustomerId), SupportErrorCodes.CustomerNotFound);

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await CustomerReferenceWrites.SaveHoldingAsync(
                dbContext,
                CustomerReferenceWrites.Collect(dbContext.ChangeTracker, TicketCustomer),
                CustomerNotFound,
                cancellationToken);
        }
        // Traducido acá y no en Application, que no referencia EF Core.
        catch (DbUpdateConcurrencyException exception)
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict",
                "The support record changed while the update was being committed.",
                exception);
        }
        // Por nombre de índice y no por 23505 (precedente TenancyUnitOfWork): cada índice único es una regla distinta.
        catch (DbUpdateException exception) when (UniqueViolation.Is(exception, SupportDbContext.TicketTypeNameIndex))
        {
            throw new ResourceConflictException(
                SupportErrorCodes.TicketTypeNameTaken,
                "A ticket type with this name already exists.",
                exception);
        }
        // Dos guardados del mismo ticket que agregan el mismo ordinal: pierde el segundo y sólo tiene que recargar, igual que con
        // la versión.
        catch (DbUpdateException exception) when (UniqueViolation.Is(exception, SupportDbContext.TicketEventOrdinalIndex))
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict",
                "The ticket trace changed while the update was being committed.",
                exception);
        }
    }

    // Copia de CustomersUnitOfWork.BeginTransactionAsync. Los SaveChanges que corran dentro siguen pasando por la traducción de
    // arriba, y el Dispose sin Commit deshace todo, también el número que emitió SupportTicketNumberGenerator.
    public async Task<ISupportTransaction> BeginTransactionAsync(CancellationToken cancellationToken) =>
        new SupportTransaction(await dbContext.Database.BeginTransactionAsync(cancellationToken));

    // El mismo protocolo que SaveHoldingAsync (candado y después existencia, en READ COMMITTED), adelantado: sin transacción
    // abierta, CustomerReferenceWrites lanza. Pedir otra vez el candado compartido en la misma transacción no espera.
    public async Task HoldCustomerReferenceAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken)
    {
        if (!await CustomerReferenceWrites.HoldAsync(dbContext.Database, tenantId, customerId, cancellationToken))
        {
            throw CustomerNotFound(new CustomerReferenceToHold(tenantId, customerId, TicketCustomer.NotFoundCode));
        }
    }

    private static SupportDomainException CustomerNotFound(CustomerReferenceToHold reference) =>
        new(reference.NotFoundCode, $"Customer '{reference.CustomerId}' was not found.");

    private sealed class SupportTransaction(IDbContextTransaction transaction) : ISupportTransaction
    {
        public Task CommitAsync(CancellationToken cancellationToken) => transaction.CommitAsync(cancellationToken);

        public ValueTask DisposeAsync() => transaction.DisposeAsync();
    }
}
