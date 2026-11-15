using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Modules.Automations.Application;
using Modules.Automations.Domain;
using Npgsql;

namespace Modules.Automations.Infrastructure.Persistence;

/// <summary>Copia de <c>PipelineUnitOfWork</c>: cada 23505 se traduce por el nombre del índice, nunca sólo por SqlState (CLAUDE.md).
/// Nombre de chatbot o de plantilla repetido y conexión ya ocupada → 409; la versión vieja y dos publicaciones que proponen el mismo
/// número (<c>IX_chatbot_versions_chatbot_number</c>) → <c>concurrency.conflict</c> (412), igual que una carrera al sincronizar la
/// ocupación. Traducido acá porque Application no
/// referencia Npgsql.</summary>
/// <remarks>Spec 2026-10-29 §4.3: si el guardado toca un chatbot o sus conexiones, la ocupación neutral de Mensajería
/// (<see cref="IChatbotConnectionSlots"/>) se sincroniza en la misma transacción, abierta acá si el llamador no abrió una. Un agente que ya
/// ocupa la conexión vuelve como 409 con el ocupante y nada queda escrito.</remarks>
internal sealed class AutomationsUnitOfWork(AutomationsDbContext dbContext, IChatbotConnectionSlots slots) : IAutomationsUnitOfWork
{
    /// <summary>Primera clave del lock de dos claves: la fecha del plan. Cualquier constante sirve mientras ningún otro lock de dos
    /// claves la use (hoy sólo está <c>TenantChangeLock.Namespace</c> = 20261008). Internal para las pruebas que toman el lock desde otra
    /// conexión.</summary>
    internal const int ConversationLockNamespace = AutomationsConversationLock.Namespace;

    /// <summary>Las propiedades de <see cref="Chatbot"/> que mueven la ocupación: el nombre que guarda la fila de Mensajería, el estado y
    /// la versión publicada que deciden si ocupa.</summary>
    private static readonly HashSet<string> SlotProperties =
        [nameof(Chatbot.Name), nameof(Chatbot.Status), nameof(Chatbot.PublishedVersionId)];

    public async Task<IAsyncDisposable> BeginTransactionAsync(CancellationToken cancellationToken) =>
        await dbContext.Database.BeginTransactionAsync(cancellationToken);

    // Precedente: OrphanUserCleanupWorker. El lock se libera con la transacción, así que sin una abierta no serializaría nada. Ruling
    // P2-R45: la variante try, que no espera; la forma de dos claves es la misma de antes.
    public async Task<bool> TryLockConversationAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        if (dbContext.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("TryLockConversationAsync exige la transacción de BeginTransactionAsync.");
        }

        // Forma de dos claves: es un espacio aparte del de una sola clave, donde viven UserLifecycleLockKey y PosSaleIdLock, así que
        // ningún hash de ellos puede chocar con el de una conversación (mismo criterio de TenantChangeLock.cs:3-13).
        var taken = await dbContext.Database.SqlQuery<bool>(
            $"SELECT pg_try_advisory_xact_lock({ConversationLockNamespace}, hashtext({conversationId.ToString()})) AS \"Value\"")
            .ToListAsync(cancellationToken);
        return taken.Single();
    }

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        var tenants = TenantsWithConnectionChanges();
        // Sin una transacción del llamador y con ocupación que sincronizar, la abre la unidad: el chatbot y su fila en Mensajería van en
        // el mismo commit. Sin cambios de conexiones (el motor, las corridas) no se abre nada nuevo.
        await using var owned = tenants.Count > 0 && dbContext.Database.CurrentTransaction is null
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            foreach (var tenantId in tenants)
            {
                var taken = await slots.SyncChatbotsAsync(tenantId, cancellationToken);
                if (taken.Count > 0)
                {
                    // Sin confirmar: al disponerse, la transacción deshace el chatbot y la ocupación juntos.
                    throw new ConnectionTakenException(taken[0]);
                }
            }

            // Con la transacción de la unidad abierta, guardar es confirmar (el handle sin confirmar deshace al disponerse).
            if (dbContext.Database.CurrentTransaction is { } transaction)
            {
                await transaction.CommitAsync(cancellationToken);
            }
        }
        // Antes que la rama de DbUpdateException: DbUpdateConcurrencyException hereda de ella.
        catch (DbUpdateConcurrencyException exception)
        {
            throw Concurrency(exception);
        }
        catch (DbUpdateException exception) when (IsUnique(exception, AutomationsDbContext.ChatbotNameIndex))
        {
            throw new ResourceConflictException(AutomationsErrorCodes.ChatbotNameTaken, "Ya existe un chatbot con ese nombre.", exception);
        }
        catch (DbUpdateException exception) when (IsUnique(exception, AutomationsDbContext.ActiveConnectionIndex))
        {
            throw new ResourceConflictException(
                AutomationsErrorCodes.ChatbotConnectionTaken, "Otro chatbot activo ya atiende esa conexión.", exception);
        }
        catch (DbUpdateException exception) when (IsUnique(exception, AutomationsDbContext.LiveRunIndex))
        {
            throw new ResourceConflictException(
                AutomationsErrorCodes.RunAlreadyLive, "La conversación ya tiene una corrida viva.", exception);
        }
        catch (DbUpdateException exception) when (IsUnique(exception, AutomationsDbContext.TemplateNameIndex))
        {
            throw new ResourceConflictException(AutomationsErrorCodes.TemplateNameTaken, "Ya existe una plantilla con ese nombre.", exception);
        }
        catch (DbUpdateException exception) when (IsUnique(exception, AutomationsDbContext.VersionNumberIndex))
        {
            throw Concurrency(exception);
        }
        // Después de las ramas de DbUpdateException: el ExecuteSqlAsync de la sincronización lanza la PostgresException sin envolver. Dos
        // guardados que igual se cruzan en connection_bots son una carrera, no un error del servidor. Precedente: CustomersWriteRaces.
        catch (Exception exception) when (IsWriteRace(exception))
        {
            throw Concurrency(exception);
        }
    }

    /// <summary>Un deadlock (<c>40P01</c>) o una falla de serialización (<c>40001</c>) en cualquier punto de la cadena de excepciones.
    /// Réplica de <c>CustomersWriteRaces.IsRace</c>: los módulos no se ven entre sí.</summary>
    internal static bool IsWriteRace(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException { SqlState: PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure })
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Los tenants de los chatbots o conexiones que este guardado agrega, borra o cambia en algo que mueve la ocupación. Un
    /// chatbot renombrado cuenta: la fila de Mensajería guarda su nombre para el 409 y la bandeja. Guardar el borrador no cuenta: el editor
    /// guarda mientras se escribe, y reescribir todas las filas del tenant en cada tecla sólo abre la puerta a que dos guardados se
    /// crucen.</summary>
    private List<Guid> TenantsWithConnectionChanges() =>
        [.. dbContext.ChangeTracker.Entries()
            .Where(entry => entry.State is EntityState.Added or EntityState.Deleted
                || (entry.State is EntityState.Modified && (entry.Entity is not Chatbot
                    || entry.Properties.Any(property => property.IsModified && SlotProperties.Contains(property.Metadata.Name)))))
            .Select(entry => entry.Entity switch
            {
                Chatbot chatbot => (Guid?)chatbot.TenantId,
                ChatbotConnection connection => connection.TenantId,
                _ => null,
            })
            .OfType<Guid>()
            .Distinct()];

    private static RequestConcurrencyException Concurrency(Exception inner) =>
        new("concurrency.conflict", "The chatbot changed while the operation was being committed.", inner);

    private static bool IsUnique(DbUpdateException exception, string indexName) =>
        exception.InnerException is PostgresException postgres &&
        postgres.SqlState == PostgresErrorCodes.UniqueViolation &&
        string.Equals(postgres.ConstraintName, indexName, StringComparison.Ordinal);
}
