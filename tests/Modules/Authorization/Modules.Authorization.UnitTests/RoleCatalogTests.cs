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

    // Spec 2026-11-16: la matriz de roles se arma con la entidad, la acción y las etiquetas de cada
    // permiso, así que moverlo de fila o de columna, o renombrarlo, es un cambio del catálogo y el
    // cliente tiene que ver otra versión.
    [Theory]
    [InlineData("a.b", "B", "manage")]
    [InlineData("a.c", "B", "read")]
    [InlineData("a.b", "Otro", "read")]
    public void TheCatalogVersionChangesWithThePermissionShape(string entity, string entityLabel, string action)
    {
        RoleDefinition[] roles = [new("advisor", "Asesor", "", "Tenancy", "low", ["a.b.read"])];
        var definition = new PermissionDefinition("a.b.read", "Ver B", "", "Tenancy", "low", RequiredModules: []);

        var baseline = new RoleCatalog(roles, [definition with { Shape = new("a.b", "B", "read") }]);
        var changed = new RoleCatalog(roles, [definition with { Shape = new(entity, entityLabel, action) }]);

        Assert.NotEqual(baseline.CatalogVersion, changed.CatalogVersion);
    }

    // Spec 2026-11-16: promover una acción a columna o renombrar una etiqueta cambia lo que pinta la
    // matriz sin tocar ningún permiso, así que el vocabulario también entra en la versión.
    [Fact]
    public void TheCatalogVersionChangesWithTheVocabulary()
    {
        RoleDefinition[] roles = [new("advisor", "Asesor", "", "Tenancy", "low", ["a.b.read"])];
        PermissionDefinition[] permissions =
        [
            new PermissionDefinition("a.b.read", "Ver B", "", "Tenancy", "low", RequiredModules: [])
            {
                Shape = new("a.b", "B", "read"),
            },
        ];
        string[] columns = ["read", "manage"];
        Dictionary<string, string> actions = new() { ["read"] = "Ver", ["manage"] = "Gestionar" };
        Dictionary<string, string> categories = new() { ["Tenancy"] = "Empresa" };

        string Version(string[] c, Dictionary<string, string> a, Dictionary<string, string> k) =>
            RoleCatalog.ComputeCatalogVersion(roles, permissions, c, a, k);

        var baseline = Version(columns, actions, categories);

        Assert.NotEqual(baseline, Version(["read", "manage", "delete"], actions, categories));
        Assert.NotEqual(baseline, Version(columns, new() { ["read"] = "Consultar", ["manage"] = "Gestionar" }, categories));
        Assert.NotEqual(baseline, Version(columns, actions, new() { ["Tenancy"] = "Organización" }));
        Assert.Equal(baseline, Version(columns, actions, categories));
    }

    // Spec 2026-11-16, Review Focus 1: el permiso del fallback no trae Shape y no puede romper el
    // catalogo. Va a «Otros» de una fila propia, con su nombre como entidad y como acción.
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
