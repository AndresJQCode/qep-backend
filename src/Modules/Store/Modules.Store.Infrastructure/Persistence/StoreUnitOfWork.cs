using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Modules.Store.Application;
using Modules.Store.Domain;

namespace Modules.Store.Infrastructure.Persistence;

/// <summary>
/// Traduce los errores de base por nombre de índice, nunca por <c>SqlState</c> solo (precedente <c>TenancyUnitOfWork</c>).
/// Vive acá y no en Application, que no referencia EF Core.
/// </summary>
internal sealed class StoreUnitOfWork(StoreDbContext dbContext) : IStoreUnitOfWork
{
    private const string SlugIndex = "IX_stores_slug";

    // Otro tenant puede ser dueño del hostname (una fila Custom): la PK de dominios es la garantía global.
    private const string DomainPrimaryKey = "PK_store_domains";

    private const string ClientRequestIndex = "IX_order_requests_tenant_client_request";

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        // Primero: DbUpdateConcurrencyException hereda de DbUpdateException.
        catch (DbUpdateConcurrencyException exception)
        {
            // Una solicitud en conflicto es un 409 (spec 5.2: la bandeja relee); los ajustes de la tienda llevan ETag y son un 412.
            if (exception.Entries.Any(entry => entry.Entity is StoreOrderRequest or StoreOrderRequestLine))
            {
                throw new ResourceConflictException(
                    StoreErrorCodes.OrderRequestConflict,
                    "The order request changed while the operation was being committed.",
                    exception);
            }

            throw new RequestConcurrencyException(
                "concurrency.conflict",
                "The store settings changed while the update was being committed.",
                exception);
        }
        catch (DbUpdateException exception) when (UniqueViolation.Is(exception, SlugIndex, DomainPrimaryKey))
        {
            throw new ResourceConflictException(
                StoreErrorCodes.SlugTaken,
                "The store address is already in use.",
                exception);
        }
        catch (DbUpdateException exception) when (UniqueViolation.Is(exception, ClientRequestIndex))
        {
            // El manejador público la atrapa y relee la solicitud que ya se guardó (Tarea 10).
            throw new ResourceConflictException(
                StoreErrorCodes.DuplicateClientRequest,
                "This order request was already received.",
                exception);
        }
    }

    public async Task<IStoreTransaction> BeginTransactionAsync(CancellationToken cancellationToken) =>
        new StoreTransaction(await dbContext.Database.BeginTransactionAsync(cancellationToken));

    private sealed class StoreTransaction(IDbContextTransaction transaction) : IStoreTransaction
    {
        public Task CommitAsync(CancellationToken cancellationToken) => transaction.CommitAsync(cancellationToken);

        public ValueTask DisposeAsync() => transaction.DisposeAsync();
    }
}
