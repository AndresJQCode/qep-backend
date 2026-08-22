using BuildingBlocks.Domain;

namespace Modules.Tenancy.Domain;

public sealed record TenantSettingsUpdatedDomainEvent(
    Guid EventId,
    DateTimeOffset OccurredAt,
    TenantId TenantId,
    long Version,
    IReadOnlyCollection<string> ChangedFields) : IDomainEvent;
