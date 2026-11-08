using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modules.Buyers.Application;
using Modules.Notifications.Application;
using Modules.Notifications.Domain;
using Modules.Notifications.Infrastructure.Persistence;

namespace Modules.Notifications.Infrastructure.Messaging;

/// <summary>
/// Lo que comparten los workers de los correos del comprador: leer el payload, armar la notificación
/// sin tenant con el destinatario del payload (la cuenta de comprador no es un usuario de Identity) y los
/// chequeos previos al envío. Un dato que falta deja la notificación en Failed con una razón fija y no
/// lanza: lanzar quemaría los reintentos de un mensaje que no se arregla solo. Ni el correo ni el token
/// ni el enlace se loguean.
/// </summary>
internal abstract class BuyerEmailDeliveryWorker(
    IServiceScopeFactory scopeFactory,
    ILogger logger)
    : OutboxDeliveryWorker(scopeFactory, logger)
{
    internal const string StoreHostInvalidReason = "store_host_invalid";

    internal const string AccountDeletedReason = "buyer_account_deleted";

    /// <summary>Si el correo lleva un token (verificar, restablecer).</summary>
    protected virtual bool RequiresToken => false;

    /// <summary>Si el correo lleva enlaces al host de la tienda.</summary>
    protected virtual bool LinksToStore => true;

    protected abstract EmailMessage Render(BuyerEmailPayload payload);

    protected override async Task<Notification> DeliverAsync(
        OutboxRecord record, DeliveryContext context, CancellationToken stoppingToken)
    {
        var payload = BuyerEmailPayloadReader.Parse(record.PayloadJson);
        var now = context.Clock.UtcNow;

        // I1 residual B2: un correo que seguía en vuelo cuando se borró la cuenta no sale, y su fila nace
        // anonimizada. El de cuenta eliminada no pasa por acá: es justamente el que tiene que salir.
        if (EventName != BuyerIntegrationEvents.AccountDeleted
            && await AccountDeletedAsync(context.Database, payload.AccountId, stoppingToken))
        {
            var skipped = Notification.CreateEmail(null, Guid.Empty, string.Empty, TemplateRef, now);
            skipped.MarkFailed(AccountDeletedReason, now);
            return skipped;
        }

        var notification = Notification.CreateEmail(
            null, payload.AccountId, payload.Email ?? string.Empty, TemplateRef, now);

        if (string.IsNullOrWhiteSpace(payload.Email))
        {
            notification.MarkFailed("recipient_email_unavailable", now);
            return notification;
        }

        if (LinksToStore && !IsValidStoreHost(payload.StoreHost))
        {
            notification.MarkFailed(StoreHostInvalidReason, now);
            return notification;
        }

        if (RequiresToken && string.IsNullOrWhiteSpace(payload.Token))
        {
            notification.MarkFailed("email_token_unavailable", now);
            return notification;
        }

        await SendAsync(context, notification, () => Render(payload), stoppingToken);
        return notification;
    }

    /// <summary>
    /// El envío pasó el chequeo de arriba, pero el borrado pudo confirmarse mientras salía el correo. Si el
    /// de cuenta eliminada ya cerró, su anonimización no alcanzó esta fila, que todavía no estaba guardada:
    /// se anonimiza acá, en el mismo SaveChanges que el processed_at.
    /// </summary>
    protected override async Task BeforeSaveAsync(
        NotificationsDbContext dbContext, Notification notification, CancellationToken cancellationToken)
    {
        if (notification.RecipientId != Guid.Empty
            && await AccountDeletedAsync(dbContext, notification.RecipientId, cancellationToken))
        {
            notification.AnonymizeRecipient();
        }
    }

    /// <summary>
    /// Si la cuenta tiene un evento de cuenta eliminada en el outbox de plataforma, entregado o no. No se
    /// busca la notificación de ese correo: al cerrar queda anonimizada, sin la cuenta. Es la misma tabla de
    /// la que este worker lee sus candidatos, así que no cruza a Buyers. El evento se purga a las 24 h
    /// (BuyerHousekeepingWorker); para entonces Buyers ya borró los correos pendientes de la cuenta en la
    /// transacción del borrado, y uno en vuelo se cierra en minutos.
    /// </summary>
    private static Task<bool> AccountDeletedAsync(
        NotificationsDbContext dbContext, Guid accountId, CancellationToken cancellationToken) =>
        dbContext.Database
            .SqlQuery<bool>(
                $"""
                SELECT EXISTS (
                    SELECT 1 FROM platform.outbox_messages
                    WHERE event_name = {BuyerIntegrationEvents.AccountDeleted}
                      AND payload->>'accountId' = {accountId.ToString()}) AS "Value"
                """)
            .SingleAsync(cancellationToken);

    private static bool IsValidStoreHost(string? host) =>
        !string.IsNullOrWhiteSpace(host) && Uri.CheckHostName(host) == UriHostNameType.Dns;
}
