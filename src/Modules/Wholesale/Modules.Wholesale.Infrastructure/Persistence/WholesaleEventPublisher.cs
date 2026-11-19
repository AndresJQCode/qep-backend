using System.Text.Json;
using Modules.Wholesale.Application;

namespace Modules.Wholesale.Infrastructure.Persistence;

/// <summary>
/// Acumula <c>wholesale.distributor-invited.v1</c> en la proyección de outbox de <see cref="WholesaleDbContext"/>, así sale con
/// el mismo commit que la invitación. Es el único lugar donde se escribe el token plano: lo lee el worker de correo de
/// Notifications por nombre con <c>JsonDocument</c>, sin referenciar Mayorista (D-18). El aviso por WhatsApp (spec 2026-11-18)
/// es otro evento, sin token ni vencimiento, que procesa el worker del aviso de este mismo módulo.
/// </summary>
internal sealed class WholesaleEventPublisher(WholesaleDbContext dbContext) : IWholesaleEventPublisher
{
    public const string DistributorInvitedEvent = "wholesale.distributor-invited.v1";

    public const string InvitationNoticeEvent = "wholesale.distributor-invitation-notice.v1";

    public const string DistributorEmailChangedEvent = "wholesale.distributor-email-changed.v1";

    public void DistributorInvited(
        Guid tenantId,
        Guid distributorId,
        string email,
        string businessName,
        string token,
        DateTimeOffset expiresAt,
        string correlationId,
        DateTimeOffset occurredAt) =>
        dbContext.Outbox.Add(new WholesaleOutboxMessage
        {
            Id = Guid.CreateVersion7(),
            EventName = DistributorInvitedEvent,
            PayloadJson = JsonSerializer.Serialize(
                new DistributorInvitedPayload(tenantId, distributorId, email, businessName, token, expiresAt)),
            CorrelationId = correlationId,
            OccurredAt = occurredAt,
        });

    public void InvitationNoticeRequested(
        Guid tenantId,
        Guid distributorId,
        string phone,
        Guid connectionId,
        Guid sentByMemberId,
        string contactName,
        string email,
        int generation,
        string correlationId,
        DateTimeOffset occurredAt) =>
        dbContext.Outbox.Add(new WholesaleOutboxMessage
        {
            Id = Guid.CreateVersion7(),
            EventName = InvitationNoticeEvent,
            PayloadJson = JsonSerializer.Serialize(new InvitationNoticePayload(
                tenantId, distributorId, phone, connectionId, sentByMemberId, contactName, email, generation)),
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
        DateTimeOffset expiresAt);

    // Sin token, sin link, sin vencimiento y sin razón social: el aviso sólo dice que la invitación llegó al correo.
    private sealed record InvitationNoticePayload(
        Guid tenantId,
        Guid distributorId,
        string phone,
        Guid connectionId,
        Guid sentByMemberId,
        string contactName,
        string email,
        int generation);
}
