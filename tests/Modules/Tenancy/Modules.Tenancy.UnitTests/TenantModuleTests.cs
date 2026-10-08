using Modules.Tenancy.Domain;

namespace Modules.Tenancy.UnitTests;

public sealed class TenantModuleTests
{
    // El origen no se valida en el dominio (spec, «Dominio»): el CHECK de la tabla es la única
    // validación, y `backfill`/`manual` sólo los escribe SQL.
    [Fact]
    public void CreateKeepsEverythingAsItArrives()
    {
        var tenantId = TenantId.New();
        var enabledAt = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

        var module = TenantModule.Create(tenantId, TenantModuleKeys.Orders, "anything", enabledAt, "nota");

        Assert.Equal(tenantId, module.TenantId);
        Assert.Same(TenantModuleKeys.Orders, module.ModuleKey);
        Assert.Equal("anything", module.Source);
        Assert.Equal(enabledAt, module.EnabledAt);
        Assert.Equal("nota", module.Note);
    }

    [Fact]
    public void CreateStartsActiveSinceItsEnabledAt()
    {
        var enabledAt = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

        var module = TenantModule.Create(TenantId.New(), TenantModuleKeys.Pos, TenantModuleSources.Operator, enabledAt, null);

        Assert.Equal(TenantModuleStatus.Active, module.Status);
        Assert.Equal(enabledAt, module.StatusChangedAt);
        Assert.Equal("operator", module.Source);
    }

    [Fact]
    public void DeactivateAndActivateMoveTheStatusAndItsDateButNotEnabledAt()
    {
        var enabledAt = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        var module = TenantModule.Create(TenantId.New(), TenantModuleKeys.Reporting, TenantModuleSources.Signup, enabledAt, null);

        module.Deactivate(enabledAt.AddDays(1));
        Assert.Equal(TenantModuleStatus.Inactive, module.Status);
        Assert.Equal(enabledAt.AddDays(1), module.StatusChangedAt);

        module.Activate(enabledAt.AddDays(2));
        Assert.Equal(TenantModuleStatus.Active, module.Status);
        Assert.Equal(enabledAt.AddDays(2), module.StatusChangedAt);
        Assert.Equal(enabledAt, module.EnabledAt);
    }

    [Fact]
    public void AChangeToTheSameStatusIsNoChanges()
    {
        var module = TenantModule.Create(TenantId.New(), TenantModuleKeys.Reporting, TenantModuleSources.Signup, DateTimeOffset.UnixEpoch, null);

        Assert.Equal("tenancy.modules.no_changes",
            Assert.Throws<TenantDomainException>(() => module.Activate(DateTimeOffset.UnixEpoch)).Code);
        module.Deactivate(DateTimeOffset.UnixEpoch);
        Assert.Equal("tenancy.modules.no_changes",
            Assert.Throws<TenantDomainException>(() => module.Deactivate(DateTimeOffset.UnixEpoch)).Code);
    }
}
