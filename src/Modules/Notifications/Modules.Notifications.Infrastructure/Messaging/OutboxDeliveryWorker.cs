using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Modules.Identity.Application;
using Modules.Notifications.Application;
using Modules.Notifications.Domain;
using Modules.Notifications.Infrastructure.Persistence;
using Modules.Tenancy.Application;

namespace Modules.Notifications.Infrastructure.Messaging;

/// <summary>
/// Lo que comparten los workers que consumen un evento del outbox de plataforma y mandan un correo
/// (spec 2026-09-13, A6): el loop, los candidatos, el reclamo, el aislamiento por mensaje y la
/// cancelación. Cada worker concreto declara su consumidor, su evento y su plantilla, y arma su
/// notificación en <see cref="DeliverAsync"/>.
///
/// Garantía: dos réplicas nunca envían el mismo mensaje a la vez. Cada mensaje se reclama en el inbox
/// propio con un lease (<see cref="InboxClaims"/>) antes de enviar. Queda un residual, el mismo de
/// antes: si el proceso muere entre el envío y el guardado, el correo sale de nuevo cuando vence el
/// lease. Infobip no recibe una clave de idempotencia, así que eso no se puede cerrar del todo.
/// </summary>
internal abstract partial class OutboxDeliveryWorker(
    IServiceScopeFactory scopeFactory,
    ILogger logger) : BackgroundService
{
    internal const int BatchSize = 20;

    /// <summary>Reclamos sin terminar que se toleran. El siguiente registra la notificación como
    /// fallida y no envía.</summary>
    internal const int MaxAttempts = 3;

    internal const string AttemptsExhaustedReason = "delivery_attempts_exhausted";

    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    /// <summary>Por encima del timeout del HttpClient de Infobip (30 s), con margen para el render y
    /// el guardado. Si venciera durante un envío, otra réplica retomaría el mensaje.</summary>
    internal static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);

    protected abstract string Consumer { get; }

    protected abstract string EventName { get; }

    protected abstract string TemplateRef { get; }

    [LoggerMessage(Level = LogLevel.Error, Message = "Delivery tick of {Consumer} failed.")]
    private static partial void LogTickFailed(ILogger logger, string consumer, Exception exception);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Outbox message {MessageId} failed in {Consumer}; it is retried when its lease expires.")]
    private static partial void LogMessageFailed(ILogger logger, Guid messageId, string consumer, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Outbox message {MessageId} was claimed {Attempts} times by {Consumer} without finishing; it is recorded as failed and not sent.")]
    private static partial void LogAttemptsExhausted(ILogger logger, Guid messageId, int attempts, string consumer);

    /// <summary>
    /// Arma la notificación del mensaje y, si corresponde, manda el correo con
    /// <see cref="SendAsync"/>. Una excepción acá, fuera del envío —un payload ilegible, un
    /// GetEmailAsync que falla—, deja el reclamo vivo: se reintenta al vencer el lease.
    /// </summary>
    protected abstract Task<Notification> DeliverAsync(
        OutboxRecord record, DeliveryContext context, CancellationToken stoppingToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            try
            {
                await DrainAsync(stoppingToken);
            }
            // Sólo el apagado sale del loop (A4). Un timeout del proveedor ya no llega hasta acá
            // (ver SendAsync), y cualquier otra cancelación se loguea como un tick fallido.
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                LogTickFailed(logger, Consumer, exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    // `internal` y no `private`: es el punto de entrada que deja probar el reclamo y el aislamiento sin
    // el PeriodicTimer de por medio (OutboxDeliveryWorkerTests, vía InternalsVisibleTo). Mismo
    // precedente que ExportJobWorker.DrainAsync.
    internal async Task DrainAsync(CancellationToken stoppingToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var dbContext = services.GetRequiredService<NotificationsDbContext>();
        var context = new DeliveryContext(
            services.GetRequiredService<IEmailChannel>(),
            services.GetRequiredService<IUserDirectory>(),
            services.GetRequiredService<ITenantDirectory>(),
            services.GetRequiredService<IClock>());

        // Candidatos: sin fila en el inbox, o con una reclamada y sin terminar cuyo lease venció.
        var now = context.Clock.UtcNow;
        var candidates = await dbContext.Outbox
            .AsNoTracking()
            .Where(record => record.EventName == EventName)
            .Where(record => !dbContext.Inbox.Any(entry =>
                entry.Consumer == Consumer
                && entry.MessageId == record.Id
                && (entry.ProcessedAt != null || entry.ClaimedUntil >= now)))
            .OrderBy(record => record.OccurredAt)
            .Take(BatchSize)
            .ToListAsync(stoppingToken);

        foreach (var record in candidates)
        {
            try
            {
                await ProcessAsync(dbContext, context, record, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // El reclamo queda vivo: al vencer el lease se reintenta, y pasado MaxAttempts se
                // corta. Ya no bloquea al resto del lote.
                LogMessageFailed(logger, record.Id, Consumer, exception);
            }
            finally
            {
                // Un mensaje que falló a mitad no le deja entidades trackeadas al siguiente. Mismo
                // precedente que OrphanUserCleanupWorker.
                dbContext.ChangeTracker.Clear();
            }
        }
    }

    /// <summary>
    /// Manda el correo y marca la notificación. Cualquier falla de envío la deja en Failed y el
    /// mensaje no se reintenta, que es la semántica de siempre. La diferencia está en el filtro: un
    /// TaskCanceledException del HttpClient sin apagado también entra acá, en vez de subir y matar el
    /// loop. Sólo el apagado sale sin marcar nada, y el lease devuelve el mensaje a la cola.
    /// </summary>
    protected static async Task SendAsync(
        DeliveryContext context,
        Notification notification,
        Func<EmailMessage> render,
        CancellationToken stoppingToken)
    {
        try
        {
            await context.Channel.SendAsync(render(), stoppingToken);
            notification.MarkSent(context.Clock.UtcNow);
        }
        catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
        {
            notification.MarkFailed(exception.Message, context.Clock.UtcNow);
        }
    }

    /// <summary>
    /// Busca el correo del destinatario en <see cref="IUserDirectory"/> y arma la notificación. Sin
    /// correo, la deja <c>Failed</c> con <c>recipient_email_unavailable</c> y no hay nada más que
    /// mandar: quien llama sólo tiene que devolver la notificación tal cual. Con correo, la deja sin
    /// terminar para que el worker siga con su propio chequeo (si lo tiene, como el token de
    /// invitación) y <see cref="SendAsync"/>. Compartido por los cinco workers (spec 2026-09-13, A6):
    /// era el mismo bloque copiado en cada uno.
    /// </summary>
    protected static async Task<(Notification Notification, string? Email)> ResolveRecipientAsync(
        DeliveryContext context, Guid tenantId, Guid subjectId, string templateRef, CancellationToken stoppingToken)
    {
        var email = await context.UserDirectory.GetEmailAsync(subjectId, stoppingToken);
        var notification = Notification.CreateEmail(
            tenantId, subjectId, email ?? string.Empty, templateRef, context.Clock.UtcNow);

        if (string.IsNullOrWhiteSpace(email))
        {
            notification.MarkFailed("recipient_email_unavailable", context.Clock.UtcNow);
            return (notification, null);
        }

        return (notification, email);
    }

    private async Task ProcessAsync(
        NotificationsDbContext dbContext,
        DeliveryContext context,
        OutboxRecord record,
        CancellationToken stoppingToken)
    {
        var attempts = await InboxClaims.TryClaimAsync(
            dbContext, Consumer, record.Id, context.Clock.UtcNow, Lease, stoppingToken);
        if (attempts is null)
        {
            // Otra réplica lo tiene, o ya se procesó.
            return;
        }

        Notification notification;
        if (attempts > MaxAttempts)
        {
            LogAttemptsExhausted(logger, record.Id, attempts.Value, Consumer);
            // Sin tenant ni destinatario: lo que falla puede ser el payload, así que no se lee.
            notification = Notification.CreateEmail(
                Guid.Empty, Guid.Empty, string.Empty, TemplateRef, context.Clock.UtcNow);
            notification.MarkFailed(AttemptsExhaustedReason, context.Clock.UtcNow);
        }
        else
        {
            notification = await DeliverAsync(record, context, stoppingToken);
        }

        // Notificación y processed_at en un solo SaveChanges. Desde acá el correo ya salió (o el mensaje
        // quedó envenenado), así que el cierre no mira el apagado: si un deploy detiene el pod justo
        // después de enviar y este guardado se corta, el mensaje queda sin terminar y la otra réplica lo
        // vuelve a mandar cuando vence el lease. El timeout de apagado del host lo sigue acotando.
        var entry = await dbContext.Inbox.SingleAsync(
            candidate => candidate.Consumer == Consumer && candidate.MessageId == record.Id,
            CancellationToken.None);
        entry.ProcessedAt = context.Clock.UtcNow;
        dbContext.Notifications.Add(notification);
        await dbContext.SaveChangesAsync(CancellationToken.None);
    }
}

/// <summary>Lo que un worker concreto necesita para armar y mandar su correo, resuelto en el scope del
/// tick.</summary>
internal sealed record DeliveryContext(
    IEmailChannel Channel, IUserDirectory UserDirectory, ITenantDirectory TenantDirectory, IClock Clock);
