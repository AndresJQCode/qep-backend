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

    // Spec 2026-11-16: la matriz de roles se arma con la entidad y la accion de cada permiso, asi que
    // moverlo de columna es un cambio del catalogo y el cliente tiene que ver otra version.
    [Fact]
    public void TheCatalogVersionChangesWithThePermissionShape()
    {
        RoleDefinition[] roles = [new("advisor", "Asesor", "", "Tenancy", "low", ["a.b.read"])];
        var definition = new PermissionDefinition("a.b.read", "Ver B", "", "Tenancy", "low", RequiredModules: []);

        var asRead = new RoleCatalog(roles, [definition with { Shape = new("a.b", "B", "read") }]);
        var asManage = new RoleCatalog(roles, [definition with { Shape = new("a.b", "B", "manage") }]);

        Assert.NotEqual(asRead.CatalogVersion, asManage.CatalogVersion);
    }

    // Spec 2026-11-16, Review Focus 1: el permiso del fallback no trae Shape y no puede romper el
    // catalogo. Va a «Otros» de una fila propia, con su nombre como entidad y como accion.
    [Fact]
    public void APermissionWithoutShapeIsPlacedInItsOwnRowUnderOther()
    {
        var catalog = new RoleCatalog(
            [new RoleDefinition("advisor", "Asesor", "", "Tenancy", "low", ["custom.thing"])],
            []);

        var placement = PermissionVocabulary.PlacementOf(Assert.Single(catalog.ListPermissions()));

        Assert.Equal(new PermissionPlacement("custom.thing", "custom.thing", "other", "custom.thing"), placement);
    }

    [Fact]
    public void APermissionWithShapeIsPlacedByItsEntityAndActionLabel()
    {
        var definition = new PermissionDefinition(
            "customers.customer.merge", "Fusionar clientes", "", "Customers", "high", RequiredModules: [])
        {
            Shape = new("customers.customer", "Clientes", "merge"),
        };

        Assert.Equal(
            new PermissionPlacement("customers.customer", "Clientes", "merge", "Fusionar"),
            PermissionVocabulary.PlacementOf(definition));
    }
}
