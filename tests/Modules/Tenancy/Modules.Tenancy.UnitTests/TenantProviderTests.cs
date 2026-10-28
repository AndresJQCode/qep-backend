using Modules.Tenancy.Domain;

namespace Modules.Tenancy.UnitTests;

public sealed class TenantProviderTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CreateStartsActive()
    {
        var tenantId = TenantId.New();

        var provider = TenantProvider.Create(tenantId, TenantProviderKeys.Zenvia, At);

        Assert.Equal(tenantId, provider.TenantId);
        Assert.Equal("zenvia", provider.ProviderKey);
        Assert.Equal(TenantModuleStatus.Active, provider.Status);
        Assert.Equal(At, provider.EnabledAt);
        Assert.Equal(At, provider.StatusChangedAt);
    }

    [Fact]
    public void DeactivatingTwiceIsNoChange()
    {
        var provider = TenantProvider.Create(TenantId.New(), TenantProviderKeys.Zenvia, At);
        provider.Deactivate(At.AddHours(1));

        var error = Assert.Throws<TenantDomainException>(() => provider.Deactivate(At.AddHours(2)));

        Assert.Equal("tenancy.providers.no_changes", error.Code);
        Assert.Equal(At.AddHours(1), provider.StatusChangedAt);
    }

    [Fact]
    public void ActivatingAnActiveProviderIsNoChange()
    {
        var provider = TenantProvider.Create(TenantId.New(), TenantProviderKeys.Zenvia, At);

        var error = Assert.Throws<TenantDomainException>(() => provider.Activate(At.AddHours(1)));

        Assert.Equal(TenantProviderChange.NoChangesCode, error.Code);
    }

    [Fact]
    public void ReactivatingMovesTheStatusChangedAtButNotTheEnabledAt()
    {
        var provider = TenantProvider.Create(TenantId.New(), TenantProviderKeys.Zenvia, At);
        provider.Deactivate(At.AddHours(1));

        provider.Activate(At.AddHours(2));

        Assert.Equal(TenantModuleStatus.Active, provider.Status);
        Assert.Equal(At, provider.EnabledAt);
        Assert.Equal(At.AddHours(2), provider.StatusChangedAt);
    }

    [Fact]
    public void ReasonsFollowTheModuleDirections()
    {
        var wrongToActive = Assert.Throws<TenantDomainException>(
            () => TenantProviderChange.EnsureReasonAllowed(TenantModuleStatus.Active, ChangeReason.Nonpayment));
        var wrongToInactive = Assert.Throws<TenantDomainException>(
            () => TenantProviderChange.EnsureReasonAllowed(TenantModuleStatus.Inactive, ChangeReason.Contract));

        Assert.Equal("tenancy.providers.reason_not_allowed", wrongToActive.Code);
        Assert.Equal("tenancy.providers.reason_not_allowed", wrongToInactive.Code);

        TenantProviderChange.EnsureReasonAllowed(TenantModuleStatus.Active, ChangeReason.Correction);
        TenantProviderChange.EnsureReasonAllowed(TenantModuleStatus.Inactive, ChangeReason.Correction);
    }

    [Fact]
    public void ForProviderWritesTheProviderKeyAndKind()
    {
        var tenantId = TenantId.New();
        var batchId = Guid.CreateVersion7();
        var actor = Guid.CreateVersion7();

        var change = TenantChange.ForProvider(
            tenantId, batchId, TenantProviderKeys.Zenvia, null, TenantModuleStatus.Active,
            ChangeReason.Contract, " Contrato firmado ", actor, At);

        Assert.Equal(tenantId, change.TenantId);
        Assert.Equal(batchId, change.BatchId);
        Assert.Equal(TenantChangeKind.Provider, change.Kind);
        Assert.Equal("zenvia", change.ProviderKey);
        Assert.Null(change.ModuleKey);
        Assert.Null(change.FromStatus);
        Assert.Equal("active", change.ToStatus);
        Assert.Equal("Contrato firmado", change.Note);
        Assert.Equal(actor, change.ActorUserId);
        Assert.Equal(At, change.OccurredAt);
    }

    [Fact]
    public void ForProviderKeepsTheFromStatusWhenThereWasARow()
    {
        var change = TenantChange.ForProvider(
            TenantId.New(), Guid.CreateVersion7(), TenantProviderKeys.Zenvia, TenantModuleStatus.Active,
            TenantModuleStatus.Inactive, ChangeReason.Nonpayment, null, Guid.CreateVersion7(), At);

        Assert.Equal("active", change.FromStatus);
        Assert.Equal("inactive", change.ToStatus);
    }

    [Fact]
    public void KindTextRoundTrips()
    {
        Assert.Equal("provider", TenantChangeVocabulary.ToText(TenantChangeKind.Provider));
        Assert.Equal(TenantChangeKind.Provider, TenantChangeVocabulary.ParseKind("provider"));
        Assert.Equal(3, (int)TenantChangeKind.Provider);
    }

    [Fact]
    public void OnlyZenviaIsKnown()
    {
        Assert.True(TenantProviderKeys.IsKnown("zenvia"));
        Assert.False(TenantProviderKeys.IsKnown("Zenvia"));
        Assert.False(TenantProviderKeys.IsKnown("whatsapp-cloud"));
        Assert.False(TenantProviderKeys.IsKnown(null));
        Assert.Equal(["zenvia"], TenantProviderKeys.All);
    }
}
