using Modules.Audit.Domain;

namespace Modules.Audit.UnitTests;

public sealed class AuditEntryTests
{
    [Fact]
    public void CreatePopulatesFieldsAndAssignsSequentialId()
    {
        var tenantId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var occurredAt = DateTimeOffset.UtcNow;

        var entry = AuditEntry.Create(
            tenantId,
            actorId,
            AuditActorType.Human,
            "tenancy.settings.updated",
            "tenant",
            tenantId.ToString(),
            "success",
            """["display_name"]""",
            "tenancy",
            occurredAt);

        Assert.NotEqual(Guid.Empty, entry.Id.Value);
        Assert.Equal(tenantId, entry.TenantId);
        Assert.Equal(actorId, entry.ActorId);
        Assert.Equal(AuditActorType.Human, entry.ActorType);
        Assert.Equal("tenancy.settings.updated", entry.Action);
        Assert.Equal("tenant", entry.ResourceType);
        Assert.Equal("success", entry.Outcome);
        Assert.Equal("""["display_name"]""", entry.ChangedFieldsJson);
        Assert.Equal("tenancy", entry.Source);
        Assert.Equal(occurredAt, entry.OccurredAt);
    }

    [Fact]
    public void CreateAllowsNullTenantForPlatformGlobalActions()
    {
        var entry = AuditEntry.Create(
            tenantId: null,
            Guid.NewGuid(),
            AuditActorType.System,
            "platform.tenant.decommissioned",
            "tenant",
            Guid.NewGuid().ToString(),
            "success",
            changedFieldsJson: "",
            "platform",
            DateTimeOffset.UtcNow);

        Assert.Null(entry.TenantId);
        // Los campos cambiados vacíos se normalizan a un array JSON vacío.
        Assert.Equal("[]", entry.ChangedFieldsJson);
    }
}
