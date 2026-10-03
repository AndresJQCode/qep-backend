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
// (consumidor, id de mensaje), y auditoría + inbox en el mismo SaveChanges que el efecto. A
// diferencia de ese, reclama cada mensaje antes de procesarlo, para acotar los reintentos (ver
// el último párrafo).
//
// Es asíncrono a propósito: RemoveMemberHandler lee el correo del usuario después del commit
// para armar su respuesta, así que borrar en el handler rompería la respuesta del remove.
//
// Qué retiene al usuario lo decide cada módulo por IUserReferenceProbe (BuildingBlocks): este
// worker no conoce a Tenancy, Quotations, Storage ni Catalog, sólo recorre las sondas registradas
// y se detiene en la primera que responde true. Una sonda nueva se registra en su módulo y entra
// sola. Auditoría y notificaciones no registran sonda: son append-only y guardan snapshot. Las
// exportaciones de Quotations tampoco: son filas transitorias que se borran solas (spec
// 2026-10-02), y una sonda retendría al usuario para siempre por algo que desaparece.
//
// Lo que un módulo guarda del usuario sin retenerlo —las membresías quitadas o vencidas de
// Tenancy— se borra antes que el usuario, por IUserReferencePurger (spec 2026-10-02). Son dos
// DbContexts y dos commits, así que no es atómico; por eso el orden: primero cada purgador
// commitea lo suyo y recién después se borra y commitea el usuario. Si algo falla después de la
// purga, en el reintento el usuario sigue ahí, las sondas siguen diciendo que no, los
// purgadores no encuentran nada y el usuario se borra. Al revés, un fallo dejaría filas
// apuntando a un usuario que ya no existe y ningún mensaje que las vuelva a mirar. Los
// purgadores corren con el lock de abajo tomado y no pueden volver a pedirlo: están en otra
// conexión y esperarían para siempre. Cada purgador devuelve cuántas filas borró, y este worker
// lo escribe en la línea de log del borrado: es el único lugar que loguea la purga.
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
// tocan la misma fila a la vez, uno pierde con una excepción de concurrencia y reintenta, y en
// el reintento ya no encuentra nada que hacer. Ninguno de los dos depende del otro.
//
// Cada mensaje corre en su propio scope de DI, y por lo tanto con sus propios DbContexts: el de
// Identity y los que usan sondas y purgadores (el de Tenancy, entre otros). Con un scope por
// lote, un mensaje que fallaba a mitad de camino dejaba entidades rastreadas en un contexto que
// este worker no puede limpiar —no conoce el de Tenancy—, y el SaveChanges del mensaje siguiente
// las commiteaba como si fueran suyas: borraba la membresía de un usuario que nunca se borró.
//
// Reintentos con backoff (spec 2026-10-02, «Reintentos y log de la purga»). Antes de procesar,
// el mensaje se reclama en el inbox propio con IdentityInboxClaims, el mismo mecanismo que
// OutboxDeliveryWorker de Notifications: una sentencia que se commitea sola, suma el intento y
// deja la fila reclamada hasta ClaimedUntil. Así el intento queda contado aunque la unidad de
// trabajo del mensaje falle y se descarte entera, y el lote no vuelve a tomar un mensaje cuyo
// reclamo sigue vivo: uno que falla siempre deja de ocupar la cabeza del lote, que se llena con
// los que sí toca. El reclamo dura LeaseFor(intento) —1, 5 y 15 minutos, la curva de reintentos
// de ExportJob (D11), y 15 de ahí en adelante—, que es a la vez el lease de quien procesa y la
// espera antes del reintento.
//
// A diferencia de Notifications, un mensaje nunca se abandona: cada reclamo procesa. Allá un
// correo que llega con media hora de atraso ya no sirve; acá borrar a un huérfano no tiene plazo,
// y abandonar convertiría una falla pasajera de un módulo —una sonda o un purgador caídos un rato,
// un deploy malo— en residuo permanente: usuarios que nunca se limpian y sólo se arreglan con SQL
// a mano. El backoff ya resuelve lo que importaba: el ruido en el log y el lote acaparado. Desde
// el intento ErrorAfterAttempts la falla se loguea en Error, así que un mensaje trabado aparece
// cada 15 minutos hasta que alguien arregle la causa, y el reintento siguiente lo procesa solo.
internal sealed partial class OrphanUserCleanupWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<OrphanUserCleanupWorker> logger) : BackgroundService
{
    internal const int BatchSize = 20;

    /// <summary>
    /// Desde qué intento una falla se loguea en Error y no en Warning. No corta nada: el mensaje
    /// se sigue reintentando cada 15 minutos. Son los cuatro intentos de ExportJob (D11): para
    /// entonces la espera ya llegó a la última de <see cref="RetryDelays"/> y lo que falla no es un
    /// tropiezo de un momento.
    /// </summary>
    internal const int ErrorAfterAttempts = 4;

    private const string Consumer = "identity.orphan-user-cleanup";
    private const string RemovedEvent = "tenancy.membership-removed.v1";

    /// <summary>
    /// La espera después del intento fallido número n es <c>RetryDelays[n - 1]</c>, la misma curva
    /// de <c>ExportJob.RetryDelays</c> (D11). Copiada y no referenciada: Identity no depende de
    /// Quotations.
    /// </summary>
    internal static readonly IReadOnlyList<TimeSpan> RetryDelays =
        [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)];

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    [LoggerMessage(Level = LogLevel.Error, Message = "Orphan user cleanup tick failed.")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Orphan user cleanup could not claim outbox message {MessageId}; it is retried on the next tick.")]
    private static partial void LogClaimFailed(ILogger logger, Exception exception, Guid messageId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Orphan user cleanup failed for outbox message {MessageId} (user {UserId}) on attempt {Attempt}; it is retried when its claim expires.")]
    private static partial void LogAttemptFailed(
        ILogger logger, Exception exception, Guid messageId, Guid? userId, int attempt);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Orphan user cleanup keeps failing for outbox message {MessageId} (user {UserId}) on attempt {Attempt}; it is retried when its claim expires.")]
    private static partial void LogAttemptFailedRepeatedly(
        ILogger logger, Exception exception, Guid messageId, Guid? userId, int attempt);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "User {UserId} kept after membership removal: still referenced by {Source}.")]
    private static partial void LogUserRetained(ILogger logger, Guid userId, string source);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "User {UserId} deleted after membership removal: no module references it. Purged rows by source: {PurgedRows}.")]
    private static partial void LogUserDeleted(ILogger logger, Guid userId, string purgedRows);

    /// <summary>
    /// Cuánto dura el reclamo número <paramref name="attempt"/>: la espera de
    /// <see cref="RetryDelays"/> que le sigue, y desde el último intento en adelante, la última.
    /// Es la misma regla que <see cref="IdentityInboxClaims"/> aplica en SQL; las pruebas avanzan
    /// el reloj con esto.
    /// </summary>
    internal static TimeSpan LeaseFor(int attempt) =>
        RetryDelays[Math.Min(attempt, RetryDelays.Count) - 1];

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
        List<OutboxRecord> pending;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            var now = scope.ServiceProvider.GetRequiredService<IClock>().UtcNow;
            // Candidatos: sin fila en el inbox, o reclamados y sin terminar con el reclamo vencido.
            // Un mensaje que espera su reintento no entra, así que no le quita lugar a otro.
            pending = await dbContext.Outbox
                .AsNoTracking()
                .Where(record => record.EventName == RemovedEvent)
                .Where(record => !dbContext.Inbox.Any(entry =>
                    entry.Consumer == Consumer
                    && entry.MessageId == record.Id
                    && (entry.ProcessedAt != null || entry.ClaimedUntil >= now)))
                .OrderBy(record => record.OccurredAt)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);
        }

        foreach (var record in pending)
        {
            // Una unidad de trabajo por mensaje: ver el comentario del encabezado.
            await using var recordScope = scopeFactory.CreateAsyncScope();
            var services = recordScope.ServiceProvider;
            int? attempts = null;
            try
            {
                var dbContext = services.GetRequiredService<IdentityDbContext>();
                var now = services.GetRequiredService<IClock>().UtcNow;
                attempts = await IdentityInboxClaims.TryClaimAsync(
                    dbContext, Consumer, record.Id, now, RetryDelays, cancellationToken);
                if (attempts is null)
                {
                    // Otra réplica lo tiene, o ya se terminó desde que se armó el lote.
                    continue;
                }

                await CleanupAsync(
                    dbContext,
                    services.GetRequiredService<IUserRepository>(),
                    services.GetServices<IUserReferenceProbe>().ToList(),
                    services.GetServices<IUserReferencePurger>().ToList(),
                    record,
                    now,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                // Un mensaje que falla (una sonda o un purgador caídos, un conflicto de
                // concurrencia con SessionRevocationWorker) no puede frenar a los demás del lote
                // ni al loop. Lo que dejó rastreado muere con su scope, así que no hay nada que
                // limpiar. El intento ya quedó contado en el reclamo, que se commiteó solo: el
                // mensaje vuelve cuando ese reclamo venza, siempre, por más veces que haya fallado.
                LogFailure(exception, record, attempts);
            }
        }
    }

    private async Task CleanupAsync(
        IdentityDbContext dbContext,
        IUserRepository users,
        IReadOnlyList<IUserReferenceProbe> probes,
        IReadOnlyList<IUserReferencePurger> purgers,
        OutboxRecord record,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var userId = ParsePayload(record.PayloadJson);

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
                // Antes que el usuario: ver el comentario del encabezado.
                var purgedRows = await PurgeAsync(purgers, userId, cancellationToken);

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
                LogUserDeleted(logger, userId, purgedRows);
            }
            else
            {
                LogUserRetained(logger, userId, retainedBy);
            }
        }

        // La fila ya existe desde el reclamo: terminarla va en el mismo SaveChanges —y en la misma
        // transacción— que el efecto, así que un mensaje procesado nunca queda sin marcar.
        var entry = await dbContext.Inbox.SingleAsync(
            candidate => candidate.Consumer == Consumer && candidate.MessageId == record.Id,
            cancellationToken);
        entry.ProcessedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }
    }

    // Sin intento (attempts en null) es que falló el reclamo mismo: no se contó nada y el mensaje
    // vuelve en el tick siguiente. Con intento, vuelve cuando su reclamo venza; desde
    // ErrorAfterAttempts en Error, para que un mensaje trabado no pase como un tropiezo más.
    private void LogFailure(Exception exception, OutboxRecord record, int? attempts)
    {
        if (attempts is not { } attempt)
        {
            LogClaimFailed(logger, exception, record.Id);
            return;
        }

        // En una variable y no dentro de la llamada al logger: CA1873.
        var userId = TryParsePayload(record.PayloadJson);
        if (attempt >= ErrorAfterAttempts)
        {
            LogAttemptFailedRepeatedly(logger, exception, record.Id, userId, attempt);
        }
        else
        {
            LogAttemptFailed(logger, exception, record.Id, userId, attempt);
        }
    }

    // En orden de registro, cada uno commitea lo suyo. Devuelve lo purgado por fuente
    // ("tenancy=1"), ya armado para el log: armarlo dentro de la llamada al logger es lo que
    // CA1873 rechaza.
    private static async Task<string> PurgeAsync(
        IReadOnlyList<IUserReferencePurger> purgers,
        Guid userId,
        CancellationToken cancellationToken)
    {
        if (purgers.Count == 0)
        {
            return "none";
        }

        var purged = new List<string>(purgers.Count);
        foreach (var purger in purgers)
        {
            var rows = await purger.PurgeAsync(userId, cancellationToken);
            purged.Add($"{purger.Source}={rows}");
        }

        return string.Join(", ", purged);
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

    // Para el log de la falla: el payload puede ser justo lo que hace fallar al mensaje, así que
    // leerlo no puede lanzar. Las excepciones son las de JsonDocument.Parse y de GetProperty
    // y GetGuid con un campo ausente, de otro tipo o con un Guid inválido.
    private static Guid? TryParsePayload(string payloadJson)
    {
        try
        {
            return ParsePayload(payloadJson);
        }
        catch (Exception exception) when (exception is JsonException
            or KeyNotFoundException
            or FormatException
            or InvalidOperationException)
        {
            return null;
        }
    }
}
