using System.Text.Json;
using Modules.Wholesale.Application;

namespace Modules.Wholesale.Infrastructure.Persistence;

/// <summary>
/// Acumula <c>wholesale.distributor-invited.v1</c> en la proyección de outbox de <see cref="WholesaleDbContext"/>, así sale con
/// el mismo commit que la invitación. Es el único lugar donde se escribe el token plano: lo lee el worker de correo de
/// Notifications por nombre con <c>JsonDocument</c>, sin referenciar Mayorista (D-18).
/// </summary>
internal sealed class WholesaleEventPublisher(WholesaleDbContext dbContext) : IWholesaleEventPublisher
{
    public const string DistributorInvitedEvent = "wholesale.distributor-invited.v1";

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

    // Nombres en minúscula como el resto de los payloads del outbox: el consumidor los lee por nombre.
    private sealed record DistributorInvitedPayload(
        Guid tenantId,
        Guid distributorId,
        string email,
        string businessName,
        string token,
        DateTimeOffset expiresAt);
}
