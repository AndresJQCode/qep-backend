using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Modules.Storage.Domain;
using Modules.Storage.Infrastructure.Persistence;

namespace Modules.Storage.Infrastructure.HrDocuments;

/// <summary>
/// Las filas que toca <see cref="HrDocumentPurge"/>, separadas para probarla sin base. Un archivo a la vez: <see cref="LockAsync"/> abre la
/// transacción y <see cref="SaveAsync"/> o <see cref="AbandonAsync"/> la cierran.
/// </summary>
internal interface IHrDocumentPurgeStore
{
    /// <summary>Bloquea la fila por id <b>y</b> tenant con <c>FOR UPDATE</c> y la carga con sus variantes; <c>null</c> si no existe en ese
    /// tenant. No filtra dueño ni estado: quien llama decide, y así puede registrar el id de otro dueño.</summary>
    Task<FileResource?> LockAsync(Guid tenantId, FileResourceId id, CancellationToken ct);

    /// <summary>Guarda lo rastreado (la purga, la auditoría y, en el último archivo, el inbox) y confirma la transacción.</summary>
    Task SaveAsync(CancellationToken ct);

    /// <summary>Deshace la transacción y descarta lo rastreado.</summary>
    Task AbandonAsync();
}

// Patrón de UnreferencedFileCandidates, con FOR UPDATE sin SKIP LOCKED: el mensaje ya es de una sola réplica (su reclamo), y si dos avisos
// distintos nombran el mismo archivo, el segundo espera al primero y lo relee Purged. El candado se sostiene mientras se pregunta a Hr y se
// borran los objetos de un solo archivo.
internal sealed class HrDocumentPurgeStore(StorageDbContext dbContext) : IHrDocumentPurgeStore
{
    private IDbContextTransaction? _transaction;

    public async Task<FileResource?> LockAsync(Guid tenantId, FileResourceId id, CancellationToken ct)
    {
        if (dbContext.Database.CurrentTransaction is null)
        {
            _transaction = await dbContext.Database.BeginTransactionAsync(ct);
        }

        var locked = await dbContext.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value"
            FROM storage.file_resources
            WHERE id = {id.Value} AND tenant_id = {tenantId}
            FOR UPDATE
            """).ToListAsync(ct);
        if (locked.Count == 0)
        {
            return null;
        }

        return await dbContext.FileResources
            .Include(file => file.Variants)
            .SingleAsync(file => file.Id == id && file.TenantId == tenantId, ct);
    }

    public async Task SaveAsync(CancellationToken ct)
    {
        await dbContext.SaveChangesAsync(ct);
        if (_transaction is not null)
        {
            await _transaction.CommitAsync(ct);
            await _transaction.DisposeAsync();
            _transaction = null;
        }

        dbContext.ChangeTracker.Clear();
    }

    public async Task AbandonAsync()
    {
        var transaction = _transaction;
        _transaction = null;
        try
        {
            if (transaction is not null)
            {
                // Sin token: un rollback que no se pide por un apagado también tiene que soltar el candado.
                await transaction.RollbackAsync(CancellationToken.None);
            }
        }
        finally
        {
            // Un rollback que falla (la conexión se cayó) no deja la transacción colgada en el contexto.
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }

            dbContext.ChangeTracker.Clear();
        }
    }
}
