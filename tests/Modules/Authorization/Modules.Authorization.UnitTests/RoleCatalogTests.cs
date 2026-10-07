using Modules.Authorization.Application;

namespace Modules.Authorization.UnitTests;

public sealed class RoleCatalogTests
{
    // Un permiso de rol sin metadata registrada sale del fallback con RequiredModules = null: sin
    // mapear, y por lo tanto enmascarado siempre (spec 2026-10-07).
    [Fact]
    public void ThePermissionMetadataFallbackDeclaresNoModules()
    {
        var catalog = new RoleCatalog(
            [new RoleDefinition("advisor", "Asesor", "", "Tenancy", "low", ["custom.thing"])],
            []);

        var permission = Assert.Single(catalog.ListPermissions());
        Assert.Equal("custom.thing", permission.Permission);
        Assert.Null(permission.RequiredModules);
    }
}
