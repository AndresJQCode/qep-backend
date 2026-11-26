using System.Text.Json;
using Modules.Wholesale.Application;

namespace Modules.Wholesale.Infrastructure.Persistence;

/// <summary>
/// Acumula <c>wholesale.distributor-invited.v1</c> en la proyección de outbox de <see cref="WholesaleDbContext"/>, así sale con
/// el mismo commit que la invitación. Es el único lugar donde se escriben el token plano y la prueba de buzón: los lee el worker
/// de correo de Notifications por nombre con <c>JsonDocument</c>, sin referenciar Mayorista (D-18). El aviso por WhatsApp es otro
/// evento, con el mismo token desde spec 2026-11-19 (<c>wholesale.distributor-invitation-link.v1</c>) y sin la prueba (spec
/// 2026-11-24), que procesa el worker del aviso de este mismo módulo.
/// </summary>
internal sealed class WholesaleEventPublisher(WholesaleDbContext dbContext) : IWholesaleEventPublisher
{
    public const string DistributorInvitedEvent = "wholesale.distributor-invited.v1";

    /// <summary>El aviso sin link (spec 2026-11-18). Ya no se escribe: el procesador lo sigue consumiendo para drenar los que
    /// quedaron en el outbox, hasta que el owner confirme que se retira.</summary>
    public const string InvitationNoticeEvent = "wholesale.distributor-invitation-notice.v1";

    /// <summary>Spec 2026-11-19 §2: el aviso con link, que reemplaza al anterior.</summary>
    public const string InvitationLinkEvent = "wholesale.distributor-invitation-link.v1";

    public const string DistributorEmailChangedEvent = "wholesale.distributor-email-changed.v1";

    public void DistributorInvited(
        Guid tenantId,
        Guid distributorId,
        string email,
        string businessName,
        string token,
        string emailProof,
        DateTimeOffset expiresAt,
        string correlationId,
        DateTimeOffset occurredAt) =>
        dbContext.Outbox.Add(new WholesaleOutboxMessage
        {
            Id = Guid.CreateVersion7(),
            EventName = DistributorInvitedEvent,
            PayloadJson = JsonSerializer.Serialize(
                new DistributorInvitedPayload(tenantId, distributorId, email, businessName, token, emailProof, expiresAt)),
            CorrelationId = correlationId,
            OccurredAt = occurredAt,
        });

    public void InvitationLinkRequested(
        Guid tenantId,
        Guid distributorId,
        string phone,
        Guid connectionId,
        Guid sentByMemberId,
        string contactName,
        string email,
        string token,
        int generation,
        string correlationId,
        DateTimeOffset occurredAt) =>
        dbContext.Outbox.Add(new WholesaleOutboxMessage
        {
            Id = Guid.CreateVersion7(),
            EventName = InvitationLinkEvent,
            PayloadJson = JsonSerializer.Serialize(new InvitationLinkPayload(
                tenantId, distributorId, phone, connectionId, sentByMemberId, contactName, email, token, generation)),
            CorrelationId = correlationId,
            OccurredAt = occurredAt,
        });

    public void DistributorEmailChanged(
        Guid tenantId,
        Guid distributorId,
        string previousEmail,
        string newEmail,
        string businessName,
        string correlationId,
        DateTimeOffset now) =>
        dbContext.Outbox.Add(new WholesaleOutboxMessage
        {
            Id = Guid.CreateVersion7(),
            EventName = DistributorEmailChangedEvent,
            PayloadJson = JsonSerializer.Serialize(
                new DistributorEmailChangedPayload(tenantId, distributorId, previousEmail, newEmail, businessName, now)),
            CorrelationId = correlationId,
            OccurredAt = now,
        });

    // Nombres en minúscula como el resto de los payloads del outbox: el consumidor los lee por nombre.
    // Sin token ni link: el aviso de correo cambiado sólo cuenta qué pasó (spec 2026-11-19 §6).
    private sealed record DistributorEmailChangedPayload(
        Guid tenantId,
        Guid distributorId,
        string previousEmail,
        string newEmail,
        string businessName,
        DateTimeOffset changedAt);

    private sealed record DistributorInvitedPayload(
        Guid tenantId,
        Guid distributorId,
        string email,
        string businessName,
        string token,
        string emailProof,
        DateTimeOffset expiresAt);

    // La forma del aviso viejo más el token (el mismo del correo): el procesador lee los dos con el mismo lector. Sin vencimiento
    // ni razón social.
    private sealed record InvitationLinkPayload(
        Guid tenantId,
        Guid distributorId,
        string phone,
        Guid connectionId,
        Guid sentByMemberId,
        string contactName,
        string email,
        string token,
        int generation);
}
