using BuildingBlocks.Application;
using Modules.Support.Domain;
using Modules.Tenancy.Application;

namespace Modules.Support.Application;

/// <summary>Devuelve si salió un PIN nuevo; el endpoint lo descarta y responde siempre 202.</summary>
public sealed record ResendPublicPinCommand(string? TenantSlug, string VisitorIp, string? Number, string? TurnstileToken)
    : ICommand<bool>;

/// <summary>
/// «Reenviar PIN» (spec §9.4, plan F10; Review Focus 4). Con Turnstile, responde lo mismo exista o no el radicado, sea anónimo, no tenga
/// destino o esté en enfriamiento: no hace nada y el endpoint dice 202. Si el ticket tiene destino registrado
/// (<see cref="SupportOpeningSender.HasDestination"/>) y pasaron <see cref="SupportTicketAccess.ResendCooldown"/> desde el último intento,
/// genera un PIN nuevo y lo manda por <see cref="SupportOpeningSender"/>, al destino del ticket, nunca a uno del visitante: el correo de
/// su foto o, si tiene conversación, el texto por su hilo con la ventana abierta (cerrada: no sale, <c>window_closed</c>). El PIN nuevo
/// reemplaza al viejo sólo si salió: si el envío falla, la persona conserva el que tenía. El intento queda anotado (el enfriamiento cuenta
/// desde él) y no desbloquea un radicado bloqueado. Nada toca el ticket ni su versión.
/// </summary>
public sealed class ResendPublicPinHandler(
    SupportPublicResolver resolver,
    ISupportCaptcha captcha,
    ISupportTicketAccessRepository accesses,
    ISupportPinHasher pinHasher,
    ISupportUnitOfWork unitOfWork,
    ITenantClock tenantClock,
    SupportOpeningDelivery openingDelivery,
    ISupportPublicOptions publicOptions)
    : ICommandHandler<ResendPublicPinCommand, bool>
{
    public async Task<bool> HandleAsync(ResendPublicPinCommand command, CancellationToken cancellationToken)
    {
        if (!await captcha.VerifyAsync(command.TurnstileToken?.Trim() ?? string.Empty, command.VisitorIp, cancellationToken))
        {
            throw new SupportDomainException(SupportErrorCodes.PublicCaptchaInvalid, "The anti-bot challenge was not passed.");
        }

        var tenant = await resolver.ResolveForConsultAsync(command.TenantSlug, cancellationToken);
        if (!SupportTicketNumber.TryParse(command.Number, out var number))
        {
            return false;
        }

        // Revisión del Bloque B (Minor 4): el enfriamiento se mira y se reclama con la fila de la clave tomada (FOR UPDATE), y el
        // reclamo (el intento anotado ya) se confirma antes de mandar: un segundo reenvío a la vez lo encuentra y no manda nada. El
        // candado no se sostiene durante el envío, que es una llamada afuera.
        SupportTicket ticket;
        SupportTicketAccess access;
        DateTimeOffset now;
        await using (var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken))
        {
            if (await accesses.LockByNumberAsync(tenant.TenantId, number, cancellationToken) is not { } match)
            {
                return false;
            }

            (ticket, access) = match;
            now = (await tenantClock.GetAsync(tenant.TenantId, cancellationToken)).UtcNow;
            if (!SupportOpeningSender.HasDestination(ticket) || !access.CanResend(now))
            {
                return false;
            }

            access.RecordDelivery(SupportOpeningSender.ChannelOf(ticket), null, now);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        // El segundo guardado toca sólo el PIN y el resultado del envío: columnas que la consulta (contador y bloqueo) no escribe. La
        // emisión nueva es `now` (Reissue la fija ahí si sale): con ella el mensaje por el hilo lleva otro ClientId que el de la apertura,
        // y Mensajería no lo toma por un reintento del anterior.
        var pin = SupportPin.Generate();
        var outcome = await openingDelivery.SendAsync(
            ticket, pin, now, SupportOpeningSender.ConsultUrl(publicOptions, tenant.Slug, ticket.DisplayNumber), cancellationToken);
        if (outcome.Sent)
        {
            access.Reissue(pinHasher.Hash(pin), now);
        }

        access.RecordDelivery(outcome.Channel, outcome.FailureCode, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return outcome.Sent;
    }
}
