using Modules.Authorization.Application;
using Modules.Authorization.Domain;

namespace Modules.Authorization.UnitTests;

public sealed class SystemRoleKeysTests
{
    // Spec 2026-11-25 (roles por tenant): sólo `admin` es de sistema; las otras cinco claves son filas que cada tenant edita.
    [Theory]
    [InlineData("admin", true)]
    [InlineData("advisor", false)]
    [InlineData("billing", false)]
    [InlineData("cashier", false)]
    [InlineData("scheduling_manager", false)]
    [InlineData("scheduling_provider", false)]
    public void OnlyAdminIsReserved(string key, bool reserved) => Assert.Equal(reserved, SystemRoleKeys.IsReserved(key));

    // La clave viaja en las membresías guardadas: cambiarla dejaría a esa gente nombrando un rol que no existe.
    [Fact]
    public void TheTemplateKeysKeepTheirValues()
    {
        Assert.Equal("advisor", RoleTemplateKeys.Advisor);
        Assert.Equal("billing", RoleTemplateKeys.Billing);
        Assert.Equal("cashier", RoleTemplateKeys.Cashier);
        Assert.Equal("scheduling_manager", RoleTemplateKeys.SchedulingManager);
        Assert.Equal("scheduling_provider", RoleTemplateKeys.SchedulingProvider);
    }

    [Fact]
    public void EveryKeyIsListedOnce() => Assert.Equal(SystemRoleKeys.All.Count, SystemRoleKeys.All.Distinct().Count());
}
