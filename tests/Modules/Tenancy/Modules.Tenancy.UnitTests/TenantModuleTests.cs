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
}
