using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
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

    // Dos primeras escrituras concurrentes de los ajustes: la segunda choca con la PK. Es la misma carrera que un If-Match viejo.
    private const string StorePrimaryKey = "PK_stores";

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        // Primero: DbUpdateConcurrencyException hereda de DbUpdateException. Los ajustes de la tienda llevan ETag: es un 412.
        catch (DbUpdateConcurrencyException exception)
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict",
                "The store settings changed while the update was being committed.",
                exception);
        }
        catch (DbUpdateException exception) when (UniqueViolation.Is(exception, StorePrimaryKey))
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict",
                "The store settings were created by another request.",
                exception);
        }
        catch (DbUpdateException exception) when (UniqueViolation.Is(exception, SlugIndex, DomainPrimaryKey))
        {
            throw new ResourceConflictException(
                StoreErrorCodes.SlugTaken,
                "The store address is already in use.",
                exception);
        }
    }
}
