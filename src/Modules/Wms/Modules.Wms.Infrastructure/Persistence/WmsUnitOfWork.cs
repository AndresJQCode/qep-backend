using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Modules.Wms.Application;
using Modules.Wms.Domain;
using Modules.Wms.Domain.Inventory;
using Modules.Wms.Domain.Warehousing;
using Npgsql;

namespace Modules.Wms.Infrastructure.Persistence;

internal sealed partial class WmsUnitOfWork(WmsDbContext dbContext, ILogger<WmsUnitOfWork> logger) : IWmsUnitOfWork
{
    /// <summary>Primera clave del lock de dos claves de Bodegas, distinta de las de Automations, Identity y Pos. La numeración de
    /// ajustes la comparte con su propio lock bloqueante.</summary>
    internal const int WmsLockNamespace = 7401;

    private const string BeforeSavingSavepoint = "wms_before_saving";

    [LoggerMessage(Level = LogLevel.Error, Message = "ledger mutation attempted")]
    private static partial void LogLedgerMutationAttempted(ILogger logger, Exception exception);

    public async Task<IAsyncDisposable> BeginTransactionAsync(CancellationToken cancellationToken) =>
        await dbContext.Database.BeginTransactionAsync(cancellationToken);

    // Precedente: AutomationsUnitOfWork. La forma de dos claves es un espacio aparte del de una sola clave, y sin transacción abierta
    // el lock no serializaría nada: se libera con ella.
    public async Task<bool> TryLockDocumentAsync(Guid documentId, CancellationToken cancellationToken)
    {
        if (dbContext.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("TryLockDocumentAsync requires the transaction from BeginTransactionAsync.");
        }

        var taken = await dbContext.Database.SqlQuery<bool>(
            $"SELECT pg_try_advisory_xact_lock({WmsLockNamespace}, hashtext({documentId.ToString()})) AS \"Value\"")
            .ToListAsync(cancellationToken);
        return taken.Single();
    }

    public async Task LockAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (dbContext.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("LockAsync requires the transaction from BeginTransactionAsync.");
        }

        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({WmsLockNamespace}, hashtext({key}))", cancellationToken);
    }

    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        var written = await PersistAsync(cancellationToken);
        if (dbContext.Database.CurrentTransaction is { } transaction)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return written;
    }

    public Task FlushAsync(CancellationToken cancellationToken) => PersistAsync(cancellationToken);

    private async Task<int> PersistAsync(CancellationToken cancellationToken)
    {
        // Propio y no el automático de EF: la consulta del 23505 necesita la transacción viva, sin depender de una opción del contexto.
        // Sin transacción explícita no hace falta: SaveChanges ya revirtió la suya.
        var transaction = dbContext.Database.CurrentTransaction;
        if (transaction is not null)
        {
            await transaction.CreateSavepointAsync(BeforeSavingSavepoint, cancellationToken);
        }

        try
        {
            return await dbContext.SaveChangesAsync(cancellationToken);
        }
        // Traducido acá y no en Application, que no referencia EF Core.
        catch (DbUpdateConcurrencyException exception)
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict",
                "The record changed while the update was being committed.",
                exception);
        }
        // Por nombre de índice y no por 23505: dos peticiones a la vez pasan la comprobación previa del handler y sólo la base las
        // distingue, y cada índice dice qué campo hay que corregir.
        catch (DbUpdateException exception) when (UniqueViolation.Is(exception, WmsDbContext.WarehouseCodeIndex))
        {
            throw new WmsDomainException(WmsErrorCodes.WarehouseCodeTaken, "A warehouse with this code already exists.", exception);
        }
        // Último recurso: el lock por tenant de quien elige o provisiona la principal serializa los cambios; sólo llega si algo marcó
        // una principal por fuera de ese camino. Se relee y se vuelve a elegir.
        catch (DbUpdateException exception) when (UniqueViolation.Is(exception, WmsDbContext.WarehousePrincipalIndex))
        {
            throw new RequestConcurrencyException("concurrency.conflict", "The principal warehouse was changed by another request.", exception);
        }
        catch (DbUpdateException exception) when (UniqueViolation.Is(exception, WmsDbContext.LocationCodeIndex))
        {
            throw new WmsDomainException(WmsErrorCodes.LocationCodeTaken, "A location with this code already exists in the warehouse.", exception);
        }
        catch (DbUpdateException exception) when (UniqueViolation.Is(exception, WmsDbContext.OwnerSelfIndex))
        {
            throw new WmsDomainException(WmsErrorCodes.OwnerSelfExists, "The tenant already has a self owner.", exception);
        }
        catch (DbUpdateException exception) when (UniqueViolation.Is(exception, WmsDbContext.UomPrimaryKey))
        {
            throw new WmsDomainException(WmsErrorCodes.UomCodeTaken, "A unit of measure with this code already exists.", exception);
        }
        catch (DbUpdateException exception) when (UniqueViolation.Is(exception, WmsDbContext.ConversionCodeIndex))
        {
            throw new WmsDomainException(WmsErrorCodes.ProfileConversionDuplicate, "The conversion code is repeated.", exception);
        }
        // Dos peticiones crearon el mismo lote a la vez: ya existe, recarga.
        catch (DbUpdateException exception) when (UniqueViolation.Is(exception, WmsDbContext.LotCodeIndex))
        {
            throw new RequestConcurrencyException("concurrency.conflict", "The lot was created by another request.", exception);
        }
        // Último recurso: el lock bloqueante de la numeración serializa las propuestas, así que sólo llega si algo numeró por fuera.
        catch (DbUpdateException exception) when (UniqueViolation.Is(exception, WmsDbContext.AdjustmentNumberIndex))
        {
            throw new RequestConcurrencyException("concurrency.conflict", "The adjustment number was taken by another request.", exception);
        }
        // Último recurso, como el de tareas: el lock de la numeración y el del operario serializan primero.
        catch (DbUpdateException exception) when (UniqueViolation.Is(exception, WmsDbContext.IncidentNumberIndex))
        {
            throw new RequestConcurrencyException("concurrency.conflict", "The incident number was taken by another request.", exception);
        }
        catch (DbUpdateException exception) when (UniqueViolation.Is(exception, WmsDbContext.IncidentOperationKeyIndex))
        {
            throw new RequestConcurrencyException("concurrency.conflict", "The incident for this operation key already exists.", exception);
        }
        // El operario ya tenía otra tarea en curso: el lock del handler lo evita al pedir la siguiente, pero reanudar una confirmación
        // tardía llega hasta acá.
        catch (DbUpdateException exception) when (UniqueViolation.Is(exception, WmsDbContext.TaskMemberInProgressIndex))
        {
            throw new TaskMemberBusyException(null, exception);
        }
        // Último recurso, como el número de ajuste: el lock de la numeración serializa las creaciones.
        catch (DbUpdateException exception) when (UniqueViolation.Is(exception, WmsDbContext.TaskNumberIndex))
        {
            throw new RequestConcurrencyException("concurrency.conflict", "The task number was taken by another request.", exception);
        }
        // Dos peticiones con la misma Idempotency-Key a la vez: la PK del recibo frena a la segunda. El filtro del API releerá el recibo
        // de la ganadora cuando el handler haya deshecho su transacción.
        catch (DbUpdateException exception) when (UniqueViolation.Is(exception, WmsDbContext.OperationReceiptPrimaryKey))
        {
            var receipt = exception.Entries.Select(entry => entry.Entity).OfType<OperationReceipt>().FirstOrDefault();
            throw new OperationReplayedException(receipt?.TenantId ?? Guid.Empty, receipt?.IdempotencyKey ?? string.Empty);
        }
        // Dos peticiones que crean el mismo perfil a la vez: las dos miraron, ninguna vio la fila y las dos arman perfil e identificadores.
        // Sólo una gana. Según el orden del guardado la perdedora tropieza con la clave del perfil o con el índice de identificadores, y
        // ambos son lo mismo: «ya existe, recarga» (412), no un 500 ni un «código ya asignado» que culpa al usuario.
        catch (DbUpdateException exception) when (UniqueViolation.Is(exception, WmsDbContext.VariantProfilePrimaryKey))
        {
            throw LostProfileRace(exception);
        }
        catch (DbUpdateException exception) when (UniqueViolation.Is(exception, WmsDbContext.IdentifierValueIndex))
        {
            // Un código de barras que ya es de otra variante también llega acá y ése sí es del usuario: lo distingue que el perfil que
            // esta petición quería crear ya exista en la base.
            if (await AProfileBeingCreatedAlreadyExistsAsync(transaction, cancellationToken))
            {
                throw LostProfileRace(exception);
            }

            throw new WmsDomainException(WmsErrorCodes.IdentifierValueTaken, "This identifier is already assigned.", exception);
        }
        // Borrar el padre de una FK RESTRICT da 23001, no 23503: ForeignKeyViolation reconoce las dos.
        catch (DbUpdateException exception) when (ForeignKeyViolation.Is(exception, WmsDbContext.LocationParentForeignKey))
        {
            throw new WmsDomainException(WmsErrorCodes.LocationHasChildren, "The location still has child locations.", exception);
        }
        // Una ubicación o un dueño con movimientos no se borra, se desactiva (§11.2): aunque el saldo sea cero, el historial la usa.
        catch (DbUpdateException exception) when (WmsDbContext.LocationReferenceForeignKeys.Any(name => ForeignKeyViolation.Is(exception, name)))
        {
            throw new WmsDomainException(WmsErrorCodes.LocationHasStock, "The location has stock movements and cannot be removed.", exception);
        }
        catch (DbUpdateException exception) when (WmsDbContext.OwnerReferenceForeignKeys.Any(name => ForeignKeyViolation.Is(exception, name)))
        {
            throw new WmsDomainException(WmsErrorCodes.OwnerHasStock, "The owner has stock movements and cannot be removed.", exception);
        }
        // §24.2: el código no tiene cómo llegar acá, el ledger sólo inserta. Si llega es un incidente, no un error del usuario.
        catch (Exception exception) when (IsLedgerMutation(exception))
        {
            LogLedgerMutationAttempted(logger, exception);
            throw new WmsDomainException(WmsErrorCodes.LedgerImmutable, "Los movimientos no se modifican: se compensan con otro movimiento.", exception);
        }
    }

    private static bool IsLedgerMutation(Exception exception) =>
        (exception as PostgresException ?? exception.InnerException as PostgresException) is { MessageText: WmsErrorCodes.LedgerImmutable };

    private static RequestConcurrencyException LostProfileRace(DbUpdateException exception) =>
        new("concurrency.conflict", "The profile was created by another request while this one was being saved.", exception);

    // Corre después de que SaveChanges falló. Sin transacción explícita, SaveChanges ya revirtió la suya; con una abierta, el 23505 la
    // dejó abortada y hay que volver al savepoint antes de consultar, o esto fallaría con 25P02 en lugar de contestar.
    private async Task<bool> AProfileBeingCreatedAlreadyExistsAsync(IDbContextTransaction? transaction, CancellationToken cancellationToken)
    {
        if (transaction is not null)
        {
            await transaction.RollbackToSavepointAsync(BeforeSavingSavepoint, cancellationToken);
        }

        var adding = dbContext.ChangeTracker.Entries<VariantLogisticsProfile>()
            .Where(entry => entry.State == EntityState.Added)
            .Select(entry => (entry.Entity.TenantId, entry.Entity.VariantId))
            .ToList();
        foreach (var (tenantId, variantId) in adding)
        {
            // Sin rastreo: la entidad Added con la misma clave no cuenta, se pregunta a la base.
            if (await dbContext.VariantProfiles.AsNoTracking().AnyAsync(p => p.TenantId == tenantId && p.VariantId == variantId, cancellationToken))
            {
                return true;
            }
        }

        return false;
    }
}
