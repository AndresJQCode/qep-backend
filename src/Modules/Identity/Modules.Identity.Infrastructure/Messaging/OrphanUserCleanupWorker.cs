using System.Text.Json;
using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Modules.Audit.Domain;
using Modules.Identity.Application;
using Modules.Identity.Domain;
using Modules.Identity.Infrastructure.Persistence;

namespace Modules.Identity.Infrastructure.Messaging;

// Consume del Outbox de plataforma el evento de membresía quitada y, si el usuario ya no deja
// huella en ningún módulo, lo borra físicamente. Mismo esqueleto que SessionRevocationWorker:
// proyección de sólo lectura del outbox, anti-join contra el inbox propio con clave
// (consumidor, id de mensaje), y auditoría + inbox en el mismo SaveChanges que el efecto.
//
// Es asíncrono a propósito: RemoveMemberHandler lee el correo del usuario después del commit
// para armar su respuesta, así que borrar en el handler rompería la respuesta del remove.
//
// Qué retiene al usuario lo decide cada módulo por IUserReferenceProbe (BuildingBlocks): este
// worker no conoce a Tenancy, Quotations ni Storage, sólo recorre las sondas registradas y se
// detiene en la primera que responde true. Una sonda nueva se registra en su módulo y entra
// sola. Auditoría y notificaciones no registran sonda: son append-only y guardan snapshot.
//
// El borrado corre bajo el advisory lock de UserLifecycleLockKey (BuildingBlocks), el mismo
// que InviteMemberHandler toma antes de aprovisionar e insertar su membresía. Las sondas se
// consultan recién con el lock tomado: una invitación que lo ganó ya commiteó su membresía y
// Tenancy la ve; una que lo pierde recién arranca cuando el usuario ya no existe y crea otro.
// Sin esto, la membresía nueva quedaba apuntando a un usuario borrado, sin FK que lo frene.
//
// Borra las sesiones explícitamente porque identity.sessions no tiene FK a users
// (IdentityDbContext.ConfigureSession); provider_links y user_preferences sí cascadean.
// SessionRevocationWorker corre en paralelo sobre el mismo evento y las revoca; si los dos
// tocan la misma fila a la vez, uno pierde con una excepción de concurrencia y reintenta en el
// tick siguiente, donde ya no encuentra nada que hacer. Ninguno de los dos depende del otro.
internal sealed partial class OrphanUserCleanupWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<OrphanUserCleanupWorker> logger) : BackgroundService
{
    private const string Consumer = "identity.orphan-user-cleanup";
    private const string RemovedEvent = "tenancy.membership-removed.v1";
    private const int BatchSize = 20;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    [LoggerMessage(Level = LogLevel.Error, Message = "Orphan user cleanup tick failed.")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Orphan user cleanup failed for outbox message {MessageId}; it will be retried.")]
    private static partial void LogMessageFailed(ILogger logger, Exception exception, Guid messageId);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "User {UserId} kept after membership removal: still referenced by {Source}.")]
    private static partial void LogUserRetained(ILogger logger, Guid userId, string source);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "User {UserId} deleted after membership removal: no module references it.")]
    private static partial void LogUserDeleted(ILogger logger, Guid userId);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            try
            {
                await ProcessBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception exception)
            {
                LogTickFailed(logger, exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task ProcessBatchAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        var probes = scope.ServiceProvider.GetServices<IUserReferenceProbe>().ToList();

        var pending = await dbContext.Outbox
            .Where(record => record.EventName == RemovedEvent)
            .Where(record => !dbContext.Inbox.Any(entry =>
                entry.Consumer == Consumer && entry.MessageId == record.Id))
            .OrderBy(record => record.OccurredAt)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        foreach (var record in pending)
        {
            try
            {
                await CleanupAsync(dbContext, users, probes, record, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                // Un mensaje que falla (una sonda caída, un conflicto de concurrencia con
                // SessionRevocationWorker) no puede frenar a los demás del lote ni al loop: se
                // descarta lo que quedó rastreado y se sigue. Sin inbox, vuelve en el próximo tick.
                LogMessageFailed(logger, exception, record.Id);
                dbContext.ChangeTracker.Clear();
            }
        }
    }

    private async Task CleanupAsync(
        IdentityDbContext dbContext,
        IUserRepository users,
        IReadOnlyList<IUserReferenceProbe> probes,
        OutboxRecord record,
        CancellationToken cancellationToken)
    {
        var userId = ParsePayload(record.PayloadJson);
        var now = DateTimeOffset.UtcNow;

        // Sin usuario no hay nada que borrar: pasa cuando el mensaje se reentrega después de
        // un borrado exitoso. Igual se marca el inbox para no volver a mirarlo.
        var user = await users.FindByIdAsync(userId, cancellationToken);
        // Sin usuario tampoco hay lock: nada que serializar. Con usuario, la transacción
        // explícita es lo que acota el lock (pg_advisory_xact_lock se libera con ella) y lo que
        // hace que un fallo a mitad de camino deshaga todo al disponerla sin commit.
        await using var transaction = user is null
            ? null
            : await dbContext.Database.BeginTransactionAsync(cancellationToken);
        if (user is not null)
        {
            await dbContext.Database.ExecuteSqlAsync(
                $"SELECT pg_advisory_xact_lock(hashtext({UserLifecycleLockKey.For(user.Email)}))",
                cancellationToken);
            var retainedBy = await FindRetainingSourceAsync(probes, userId, cancellationToken);
            if (retainedBy is null)
            {
                var sessions = await dbContext.Sessions
                    .Where(session => session.UserId == new UserId(userId))
                    .ToListAsync(cancellationToken);
                dbContext.Sessions.RemoveRange(sessions);
                users.Remove(user);
                dbContext.AuditEntries.Add(AuditEntry.Create(
                    tenantId: null,
                    userId,
                    AuditActorType.System,
                    "identity.user.deleted",
                    "user",
                    userId.ToString(),
                    "success",
                    "[]",
                    "identity",
                    now));
                LogUserDeleted(logger, userId);
            }
            else
            {
                LogUserRetained(logger, userId, retainedBy);
            }
        }

        dbContext.Inbox.Add(new IdentityInboxMessage
        {
            Consumer = Consumer,
            MessageId = record.Id,
            ProcessedAt = now,
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }
    }

    // Secuencial y cortando en la primera que retiene: cada sonda es una consulta a otro
    // módulo, y en el caso normal —la persona sigue en otro tenant— Tenancy responde primero.
    private static async Task<string?> FindRetainingSourceAsync(
        IReadOnlyList<IUserReferenceProbe> probes,
        Guid userId,
        CancellationToken cancellationToken)
    {
        foreach (var probe in probes)
        {
            if (await probe.HasReferencesAsync(userId, cancellationToken))
            {
                return probe.Source;
            }
        }

        return null;
    }

    private static Guid ParsePayload(string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);
        return document.RootElement.GetProperty("userId").GetGuid();
    }
}
