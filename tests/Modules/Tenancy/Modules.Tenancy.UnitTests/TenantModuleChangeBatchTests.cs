using Modules.Tenancy.Domain;
using static Modules.Tenancy.Domain.TenantModuleKeys;

namespace Modules.Tenancy.UnitTests;

/// <summary>Spec 2026-10-08 §3, «Regla de consistencia (exacta)» y los cinco rechazos del lote.</summary>
public sealed class TenantModuleChangeBatchTests
{
    private const TenantModuleStatus On = TenantModuleStatus.Active;
    private const TenantModuleStatus Off = TenantModuleStatus.Inactive;

    private static Dictionary<TenantModuleKey, TenantModuleStatus> SignupState() =>
        DefaultForNewTenants.ToDictionary(key => key, _ => On);

    private static TenantDomainException Rejected(
        Dictionary<TenantModuleKey, TenantModuleStatus> stored, ChangeReason reason, params RequestedModuleChange[] changes) =>
        Assert.Throws<TenantDomainException>(() => TenantModuleChangeBatch.Plan(stored, changes, reason));

    [Fact]
    public void AnEmptyBatchIsNoChanges() =>
        Assert.Equal("tenancy.modules.no_changes", Rejected(SignupState(), ChangeReason.Correction).Code);

    [Fact]
    public void TheSameKeyTwiceIsADuplicate() =>
        Assert.Equal("tenancy.modules.duplicate_key", Rejected(
            SignupState(), ChangeReason.Cancellation, new(Reporting, Off), new(Reporting, Off)).Code);

    [Fact]
    public void ActivatingAndDeactivatingInOneBatchIsMixed() =>
        Assert.Equal("tenancy.modules.mixed_directions", Rejected(
            SignupState(), ChangeReason.Correction, new(Reporting, Off), new(Pos, On)).Code);

    [Theory]
    [InlineData(TenantModuleStatus.Active, ChangeReason.Nonpayment)]
    [InlineData(TenantModuleStatus.Active, ChangeReason.Cancellation)]
    [InlineData(TenantModuleStatus.Inactive, ChangeReason.Contract)]
    [InlineData(TenantModuleStatus.Inactive, ChangeReason.Courtesy)]
    public void TheReasonMustMatchTheDirection(TenantModuleStatus to, ChangeReason reason)
    {
        var key = to == On ? Pos : Reporting;
        Assert.Equal("tenancy.modules.reason_not_allowed", Rejected(SignupState(), reason, [new(key, to)]).Code);
    }

    [Theory]
    [InlineData(TenantModuleStatus.Active)]
    [InlineData(TenantModuleStatus.Inactive)]
    public void CorrectionIsAllowedBothWays(TenantModuleStatus to)
    {
        var key = to == On ? Pos : Reporting;
        Assert.Single(TenantModuleChangeBatch.Plan(SignupState(), [new(key, to)], ChangeReason.Correction));
    }

    [Fact]
    public void ActivatingAnActiveModuleIsNoChanges() =>
        Assert.Equal("tenancy.modules.no_changes", Rejected(SignupState(), ChangeReason.Contract, [new(Reporting, On)]).Code);

    [Fact]
    public void DeactivatingAKeyWithoutRowIsNoChanges() =>
        Assert.Equal("tenancy.modules.no_changes", Rejected(SignupState(), ChangeReason.Cancellation, [new(Pos, Off)]).Code);

    [Fact]
    public void DeactivatingAnInactiveModuleIsNoChanges()
    {
        var stored = SignupState();
        stored[Reporting] = Off;
        Assert.Equal("tenancy.modules.no_changes", Rejected(stored, ChangeReason.Cancellation, [new(Reporting, Off)]).Code);
    }

    // §3, ejemplo 1: d ∈ L. Decisión P4 del plan: el único par roto es quotations -> customers.
    [Fact]
    public void DeactivatingCustomersWithQuotationsActiveOutsideTheBatchIsInconsistent()
    {
        var error = Rejected(SignupState(), ChangeReason.Cancellation, [new(Customers, Off)]);

        Assert.Equal("tenancy.modules.inconsistent_dependencies", error.Code);
        Assert.Contains("quotations -> customers", error.Message, StringComparison.Ordinal);
    }

    // §3, ejemplo 2: m ∈ L.
    [Fact]
    public void ActivatingQuotationsWithoutCustomersIsInconsistent()
    {
        var stored = new Dictionary<TenantModuleKey, TenantModuleStatus> { [Catalog] = On, [Companies] = On };

        Assert.Equal("tenancy.modules.inconsistent_dependencies",
            Rejected(stored, ChangeReason.Contract, [new(Quotations, On)]).Code);
    }

    // §3, ejemplo 3 (D9): un par roto heredado que el lote no toca no bloquea.
    [Fact]
    public void AnInheritedBrokenPairDoesNotBlockActivatingPos()
    {
        var stored = SignupState();
        stored[Quotations] = Off;   // orders queda activo sin quotations: heredado del SQL manual

        var plan = TenantModuleChangeBatch.Plan(stored, [new(Pos, On)], ChangeReason.Contract);

        Assert.Equal([new PlannedModuleChange(Pos, null, On)], plan);
    }

    [Fact]
    public void TheWholeCascadeInOneBatchIsAcceptedInCatalogOrder()
    {
        var plan = TenantModuleChangeBatch.Plan(
            SignupState(), [new(Orders, Off), new(Customers, Off), new(Quotations, Off)], ChangeReason.Cancellation);

        Assert.Equal(
            [new PlannedModuleChange(Customers, On, Off), new PlannedModuleChange(Quotations, On, Off), new PlannedModuleChange(Orders, On, Off)],
            plan);
    }

    [Fact]
    public void ReactivatingAnInactiveRowPlansFromInactive()
    {
        var stored = SignupState();
        stored[Reporting] = Off;

        Assert.Equal([new PlannedModuleChange(Reporting, Off, On)],
            TenantModuleChangeBatch.Plan(stored, [new(Reporting, On)], ChangeReason.Courtesy));
    }
}
