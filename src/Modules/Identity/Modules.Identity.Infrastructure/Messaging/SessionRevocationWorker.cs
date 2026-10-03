using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Modules.Audit.Domain;
using Modules.Identity.Domain;
using Modules.Identity.Infrastructure.Persistence;

namespace Modules.Identity.Infrastructure.Messaging;

// Consume del Outbox de plataforma los eventos de integración de membresía suspendida o
// quitada y revoca toda sesión activa del usuario afectado. Es idempotente por el inbox
// propio de este módulo, con clave (consumidor, id de mensaje de outbox): un mensaje
// reentregado se saltea. Session.Revoke es idempotente en sí mismo, así que procesar dos
// veces tras una caída también es seguro, no sólo la entrega única.
//
// Deliberadamente revoca TODAS las sesiones del usuario, no sólo las del tenant afectado:
// el token de sesión no lleva contexto de tenant (tenant y permisos se resuelven en vivo
// por request desde el estado de la membresía, ver ExternalClaimsTransformation), así que
// no hay sesión por tenant a la cual acotar esto. Desloguear al usuario de todos los
// tenants ante una suspensión/baja de un solo tenant es más amplio que lo estrictamente
// necesario, pero más simple y seguro — ver el ADR de cookie de sesión por el trade-off.
//
// Una revocación que se atrasa es defensa en profundidad, no un acceso abierto. El acceso al
// tenant ya se corta en el request siguiente a la suspensión o la baja, sin esperar a este
// worker: ExternalClaimsTransformation.ResolveTenantAndPermissionsAsync le pide los permisos a
// IAuthorizationService en cada request, y MembershipDirectory.FindActiveRolesAsync sólo los da
// para una membresía Active; con Suspended o Removed no hay claim de tenant ni de permisos, y todo
// endpoint del tenant lo rechaza. Lo que la sesión viva todavía permite es lo que no pide un
// tenant, como /auth/me, y los tenants donde la persona sigue activa.
//
// Mismo esqueleto que OrphanUserCleanupWorker, compartido en ClaimedOutboxConsumer (spec
// 2026-10-02, «Revocación de sesiones»): cada mensaje se reclama antes de procesarlo y corre en
// su propio scope con su propio try/catch. Antes el lote compartía un scope y no tenía catch por
// mensaje: el primero que lanzaba cortaba el lote entero, y como el lote se ordena por llegada,
// quedaba primero en cada tick y ninguna revocación posterior se hacía nunca. Ahora un mensaje
// que falla espera su reintento sin quitarle lugar a nadie, y nunca se abandona: una revocación
// abandonada dejaría viva una sesión que tenía que morir.
//
// La curva es más corta que la del huérfano (5 s, 30 s y 5 minutos, y 5 de ahí en adelante): acá
// el atraso es una sesión que sigue viva, y reintentar cuesta una consulta y un UPDATE por sesión.
// La primera espera es casi un tick porque la falla más probable es pasajera: un conflicto de
// concurrencia con OrphanUserCleanupWorker sobre las mismas sesiones, que se resuelve en el
// reintento; antes del reclamo, ese reintento llegaba a los 3 s del tick siguiente. Desde el
// intento ErrorAfterAttempts —el primero que deja por delante la espera de 5 minutos— la falla
// va en Error.
internal sealed partial class SessionRevocationWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<SessionRevocationWorker> logger) : BackgroundService
{
    /// <summary>
    /// Desde qué intento una falla se loguea en Error y no en Warning. No corta nada: el mensaje
    /// se sigue reintentando cada 5 minutos. Tres y no los cuatro del huérfano: el tercer intento
    /// es el primero que deja la espera más larga por delante, y para entonces la causa ya
    /// sobrevivió a dos reintentos rápidos (5 y 30 segundos), así que no es un tropiezo de un
    /// momento: una sesión que debía morir va a seguir viva por lo menos cinco minutos más, y eso
    /// tiene que verse.
    /// </summary>
    internal const int ErrorAfterAttempts = 3;

    internal const string Consumer = "identity.session-revocation";
    internal const string SuspendedEvent = "tenancy.membership-suspended.v1";
    internal const string RemovedEvent = "tenancy.membership-removed.v1";

    /// <summary>La espera después del intento fallido número n es <c>RetryDelays[n - 1]</c>, y
    /// desde el último, el último. Se le pasa una sola vez a <see cref="Claims"/>, que es quien la
    /// aplica.</summary>
    private static readonly IReadOnlyList<TimeSpan> RetryDelays =
        [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5)];

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    /// <summary>El esqueleto compartido, con la curva de este worker. Interno para que las pruebas
    /// lean la curva de la instancia que corre.</summary>
    internal ClaimedOutboxConsumer Claims { get; } =
        new(scopeFactory, Consumer, [SuspendedEvent, RemovedEvent], RetryDelays);

    [LoggerMessage(Level = LogLevel.Error, Message = "Session revocation tick failed.")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Session revocation could not claim outbox message {MessageId}; it is retried on the next tick.")]
    private static partial void LogClaimFailed(ILogger logger, Exception exception, Guid messageId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Session revocation failed for outbox message {MessageId} (user {UserId}) on attempt {Attempt}; it is retried when its claim expires.")]
    private static partial void LogAttemptFailed(
        ILogger logger, Exception exception, Guid messageId, Guid? userId, int attempt);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Session revocation keeps failing for outbox message {MessageId} (user {UserId}) on attempt {Attempt}; it is retried when its claim expires.")]
    private static partial void LogAttemptFailedRepeatedly(
        ILogger logger, Exception exception, Guid messageId, Guid? userId, int attempt);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            try
            {
                await Claims.ProcessBatchAsync(RevokeAsync, LogFailure, stoppingToken);
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

    // Corre ya reclamado y en su propio scope (ClaimedOutboxConsumer). La hora es la del IClock
    // del reclamo: la misma para el revoked_at, la auditoría y el inbox.
    private async Task RevokeAsync(ClaimedMessage message, CancellationToken cancellationToken)
    {
        var dbContext = message.DbContext;
        var record = message.Record;
        var userId = MembershipEventPayload.ReadUserId(record.PayloadJson);
        var reason = record.EventName == SuspendedEvent
            ? "membership_suspended"
            : "membership_removed";
        var now = message.Now;

        var activeSessions = await dbContext.Sessions
            .Where(session => session.UserId == new UserId(userId) && session.RevokedAt == null)
            .ToListAsync(cancellationToken);

        foreach (var session in activeSessions)
        {
            session.Revoke(now, reason);
            dbContext.AuditEntries.Add(AuditEntry.Create(
                tenantId: null,
                userId,
                AuditActorType.System,
                "identity.session.revoked",
                "session",
                session.Id.ToString(),
                "success",
                "[]",
                "identity",
                now));
        }

        await Claims.MarkProcessedAsync(message, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    // Sin intento (attempts en null) es que falló el reclamo mismo: no se contó nada y el mensaje
    // vuelve en el tick siguiente. Con intento, vuelve cuando su reclamo venza; desde
    // ErrorAfterAttempts en Error.
    private void LogFailure(Exception exception, OutboxRecord record, int? attempts)
    {
        if (attempts is not { } attempt)
        {
            LogClaimFailed(logger, exception, record.Id);
            return;
        }

        // En una variable y no dentro de la llamada al logger: CA1873.
        var userId = MembershipEventPayload.TryReadUserId(record.PayloadJson);
        if (attempt >= ErrorAfterAttempts)
        {
            LogAttemptFailedRepeatedly(logger, exception, record.Id, userId, attempt);
        }
        else
        {
            LogAttemptFailed(logger, exception, record.Id, userId, attempt);
        }
    }
}
