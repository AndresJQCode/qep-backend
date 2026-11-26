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
    /// Al cerrar el mensaje de cuenta eliminada, el correo y la cuenta salen de toda notificación del
    /// comprador, ésta incluida (spec, decisión 9: sólo se conserva el registro de la autorización). Pasa
    /// tanto si el envío salió como si falló: los dos cierran el mensaje (processed_at, sin reintento), así
    /// que no habrá otra oportunidad. La fila fallida conserva el estado y la razón, que es la evidencia del
    /// fallo. Las filas no se borran, para no perder las estadísticas de envío. Va en el mismo SaveChanges
    /// que la notificación y el processed_at. Sin cuenta (intentos agotados: el payload no se lee) no hay a
    /// quién anonimizar.
    /// </summary>
    protected override async Task BeforeSaveAsync(
        NotificationsDbContext dbContext, Notification notification, CancellationToken cancellationToken)
    {
        if (notification.RecipientId == Guid.Empty)
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
