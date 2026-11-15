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
/// genera un PIN nuevo y lo manda por <see cref="SupportOpeningSender"/>, al destino del ticket, nunca a uno del visitante. El PIN nuevo
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
    SupportOpeningSender openingSender,
    ISupportPublicOptions publicOptions)
    : ICommandHandler<ResendPublicPinCommand, bool>
{
    public async Task<bool> HandleAsync(ResendPublicPinCommand command, CancellationToken cancellationToken)
    {
        if (!await captcha.VerifyAsync(command.TurnstileToken?.Trim() ?? string.Empty, command.VisitorIp, cancellationToken))
        {
            throw new SupportDomainException(SupportErrorCodes.PublicCaptchaInvalid, "The anti-bot challenge was not passed.");
        }

        var tenant = await resolver.ResolveAsync(command.TenantSlug, cancellationToken);
        if (!SupportTicketNumber.TryParse(command.Number, out var number)
            || await accesses.FindByNumberAsync(tenant.TenantId, number, cancellationToken) is not { } match)
        {
            return false;
        }

        var (ticket, access) = match;
        var now = (await tenantClock.GetAsync(tenant.TenantId, cancellationToken)).UtcNow;
        if (!SupportOpeningSender.HasDestination(ticket) || !access.CanResend(now))
        {
            return false;
        }

        var pin = SupportPin.Generate();
        var outcome = await openingSender.SendAsync(
            ticket, pin, SupportOpeningSender.ConsultUrl(publicOptions, tenant.Slug, ticket.DisplayNumber), cancellationToken);
        if (outcome.Sent)
        {
            access.Reissue(pinHasher.Hash(pin), now);
        }

        access.RecordDelivery(outcome.Channel, outcome.FailureCode, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return outcome.Sent;
    }
}
