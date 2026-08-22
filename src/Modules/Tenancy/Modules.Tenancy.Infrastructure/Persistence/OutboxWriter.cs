using System.Text.Json;
using BuildingBlocks.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Infrastructure.Persistence;

internal sealed class OutboxWriter(TenancyDbContext dbContext) : IOutboxWriter
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public void Add(IDomainEvent domainEvent, string correlationId)
    {
        var eventName = domainEvent switch
        {
            TenantSettingsUpdatedDomainEvent => "tenancy.tenant-settings-updated.v1",
            MembershipInvitedDomainEvent => "tenancy.membership-invited.v1",
            MembershipAcceptedDomainEvent => "tenancy.membership-accepted.v1",
            MembershipSuspendedDomainEvent => "tenancy.membership-suspended.v1",
            MembershipRemovedDomainEvent => "tenancy.membership-removed.v1",
            MembershipReactivatedDomainEvent => "tenancy.membership-reactivated.v1",
            MembershipRolesChangedDomainEvent => "tenancy.membership-roles-changed.v1",
            _ => throw new InvalidOperationException(
                $"Domain event '{domainEvent.GetType().Name}' has no integration-event mapping.")
        };

        dbContext.OutboxMessages.Add(new OutboxMessage
        {
            Id = domainEvent.EventId,
            EventName = eventName,
            PayloadJson = JsonSerializer.Serialize(domainEvent, domainEvent.GetType(), SerializerOptions),
            CorrelationId = correlationId,
            OccurredAt = domainEvent.OccurredAt
        });
    }
}
