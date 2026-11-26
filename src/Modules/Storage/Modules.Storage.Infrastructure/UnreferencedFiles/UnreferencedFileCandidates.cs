using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Modules.Storage.Domain;
using Modules.Storage.Infrastructure.Persistence;

namespace Modules.Storage.Infrastructure.UnreferencedFiles;

/// <summary>Un archivo candidato a soltarse: lo que el barrido necesita para agrupar por tenant y seguir el cursor.</summary>
internal sealed record UnreferencedFileCandidate(Guid TenantId, FileResourceId Id, FileOwnerType OwnerType, DateTimeOffset CreatedAt);

/// <summary>
/// Las filas que leen y reclaman los barridos de archivos sin uso (<see cref="UnreferencedFileSweep"/>) y el consumidor de avisos de
/// liberación, separadas para poder probarlos sin base. Un reclamo a la vez: <see cref="ClaimAsync"/> abre la transacción y
/// <see cref="CommitAsync"/> o <see cref="AbandonAsync"/> la cierran.
/// </summary>
internal interface IUnreferencedFileCandidates
{
    /// <summary>Archivos vivos (ni <c>Deleted</c> ni <c>Purged</c>) de <paramref name="ownerType"/> creados antes de
    /// <paramref name="createdBefore"/>, del más viejo al más nuevo por <c>(created_at, id)</c>, después de <paramref name="after"/>.</summary>
    Task<IReadOnlyList<UnreferencedFileCandidate>> ListStaleAsync(
        FileOwnerType ownerType, DateTimeOffset createdBefore, UnreferencedFileCandidate? after, int take, CancellationToken cancellationToken);

    /// <summary>Bloquea la fila si sigue viva y es de <paramref name="ownerType"/> y de ese tenant, y la carga con sus variantes. <c>null</c>
    /// si ya no lo es o si otra réplica la tiene bloqueada; en ese caso no queda transacción abierta.</summary>
    Task<FileResource?> ClaimAsync(FileOwnerType ownerType, Guid tenantId, FileResourceId id, CancellationToken cancellationToken);

    /// <summary>Guarda lo rastreado (borrado lógico y auditoría) y confirma la transacción del reclamo.</summary>
    Task CommitAsync(CancellationToken cancellationToken);

    /// <summary>Deshace la transacción del reclamo y descarta lo rastreado.</summary>
    Task AbandonAsync();
}

// El reclamo es FOR UPDATE SKIP LOCKED sobre la fila y relee el estado bajo el candado: dos réplicas que barren a la vez no sueltan ni
// auditan dos veces el mismo archivo. La que llega segunda lo salta (bloqueado) o ya lo ve Deleted. El candado se sostiene mientras se
// borran las copias públicas de un solo archivo: dos o tres DELETE a R2.
internal sealed class UnreferencedFileCandidates(StorageDbContext dbContext) : IUnreferencedFileCandidates
{
    private static readonly string Deleted = nameof(FileResourceStatus.Deleted);
    private static readonly string Purged = nameof(FileResourceStatus.Purged);

    private IDbContextTransaction? _transaction;

    public async Task<IReadOnlyList<UnreferencedFileCandidate>> ListStaleAsync(
        FileOwnerType ownerType, DateTimeOffset createdBefore, UnreferencedFileCandidate? after, int take, CancellationToken cancellationToken)
    {
        var owner = ownerType.ToString();
        // Npgsql sólo escribe un timestamptz desde un DateTimeOffset en UTC.
        var cutoff = createdBefore.ToUniversalTime();
        var rows = after is null
            ? await dbContext.Database.SqlQuery<CandidateRow>($"""
                SELECT tenant_id AS "TenantId", id AS "Id", created_at AS "CreatedAt"
                FROM storage.file_resources
                WHERE owner_type = {owner} AND status NOT IN ({Deleted}, {Purged}) AND created_at < {cutoff}
                ORDER BY created_at, id
                LIMIT {take}
                """).ToListAsync(cancellationToken)
            : await dbContext.Database.SqlQuery<CandidateRow>($"""
                SELECT tenant_id AS "TenantId", id AS "Id", created_at AS "CreatedAt"
                FROM storage.file_resources
                WHERE owner_type = {owner} AND status NOT IN ({Deleted}, {Purged}) AND created_at < {cutoff}
                  AND (created_at, id) > ({after.CreatedAt.ToUniversalTime()}, {after.Id.Value})
                ORDER BY created_at, id
                LIMIT {take}
                """).ToListAsync(cancellationToken);
        return rows
            .Select(row => new UnreferencedFileCandidate(row.TenantId, new FileResourceId(row.Id), ownerType, row.CreatedAt))
            .ToList();
    }

    public async Task<FileResource?> ClaimAsync(FileOwnerType ownerType, Guid tenantId, FileResourceId id, CancellationToken cancellationToken)
    {
        var owner = ownerType.ToString();
        _transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var locked = await dbContext.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value"
            FROM storage.file_resources
            WHERE id = {id.Value} AND tenant_id = {tenantId} AND owner_type = {owner} AND status NOT IN ({Deleted}, {Purged})
            FOR UPDATE SKIP LOCKED
            """).ToListAsync(cancellationToken);
        if (locked.Count == 0)
        {
            await AbandonAsync();
            return null;
        }

        return await dbContext.FileResources
            .Include(file => file.Variants)
            .SingleAsync(file => file.Id == id && file.TenantId == tenantId, cancellationToken);
    }

    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        await dbContext.SaveChangesAsync(cancellationToken);
        if (_transaction is not null)
        {
            await _transaction.CommitAsync(cancellationToken);
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
            // Un rollback que falla (la conexión se cayó) no deja la transacción colgada en el contexto; si quedara, cada reclamo
            // siguiente de la corrida fallaría al abrir la suya. Al desecharla el contexto la olvida y la conexión la descarta.
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }

            dbContext.ChangeTracker.Clear();
        }
    }

    private sealed record CandidateRow(Guid TenantId, Guid Id, DateTimeOffset CreatedAt);
}
