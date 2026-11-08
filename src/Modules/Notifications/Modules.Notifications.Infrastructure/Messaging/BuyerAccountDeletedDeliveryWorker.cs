using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modules.Buyers.Application;
using Modules.Notifications.Application;
using Modules.Notifications.Domain;
using Modules.Notifications.Infrastructure.Persistence;

namespace Modules.Notifications.Infrastructure.Messaging;

// Consume la confirmación de cuenta eliminada del comprador. No lleva token ni enlaces.
internal sealed class BuyerAccountDeletedDeliveryWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<BuyerAccountDeletedDeliveryWorker> logger)
    : BuyerEmailDeliveryWorker(scopeFactory, logger)
{
    protected override string Consumer => "notifications.buyer-account-deleted-email";

    protected override string EventName => BuyerIntegrationEvents.AccountDeleted;

    protected override string TemplateRef => BuyerAccountDeletedEmailTemplate.TemplateRef;

    protected override bool LinksToStore => false;

    protected override EmailMessage Render(BuyerEmailPayload payload) =>
        BuyerAccountDeletedEmailTemplate.Render(payload.Email, payload.StoreName);

    /// <summary>
    /// Con el correo de cuenta eliminada ya enviado, el correo y la cuenta salen de toda notificación del
    /// comprador, ésta incluida (spec, decisión 9: sólo se conserva el registro de la autorización). Las
    /// filas no se borran, para no perder las estadísticas de envío. Va en el mismo SaveChanges que la
    /// notificación y el processed_at. Si el envío falló, no se anonimiza: la fila es la evidencia del fallo.
    /// </summary>
    protected override async Task BeforeSaveAsync(
        NotificationsDbContext dbContext, Notification notification, CancellationToken cancellationToken)
    {
        if (notification.Status != NotificationStatus.Sent || notification.RecipientId == Guid.Empty)
        {
            return;
        }

        var accountId = notification.RecipientId;
        var earlier = await dbContext.Notifications
            .Where(row => row.TenantId == null
                && row.RecipientId == accountId
                && row.TemplateRef.StartsWith(BuyerAccountDeletedEmailTemplate.BuyerTemplateRefPrefix))
            .ToListAsync(cancellationToken);

        foreach (var row in earlier)
        {
            row.AnonymizeRecipient();
        }

        notification.AnonymizeRecipient();
    }
}
