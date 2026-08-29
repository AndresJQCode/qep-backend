using Modules.Authorization.Domain;

namespace Modules.Authorization.UnitTests;

public sealed class RoleTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();

    private static Role ARole(params string[] permissions) =>
        Role.Create(
            RoleId.New(),
            Tenant,
            "ventas-junior",
            "Ventas junior",
            "Cotiza y consulta clientes, sin tocar la configuracion.",
            permissions.Length == 0 ? ["advisorship.read"] : permissions,
            DateTimeOffset.UnixEpoch);

    [Fact]
    public void CreateNormalizesTheKeyAndStartsAtVersionOne()
    {
        var role = Role.Create(
            RoleId.New(),
            Tenant,
            "  Ventas-Junior  ",
            " Ventas junior ",
            " Descripcion ",
            ["advisorship.read"],
            DateTimeOffset.UnixEpoch);

        // La clave viaja en la membresia y se compara ordinal contra el catalogo. Sin
        // normalizar, "Ventas-Junior" y "ventas-junior" son dos roles distintos que se ven
        // iguales, y una membresia apunta a uno de los dos sin forma de saber cual.
        Assert.Equal("ventas-junior", role.Key);
        Assert.Equal("Ventas junior", role.DisplayName);
        Assert.Equal("Descripcion", role.Description);
        Assert.Equal(1, role.Version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("con espacio")]
    [InlineData("acentuada-ñ")]
    [InlineData("Mayuscula_guion_bajo")]
    public void CreateRejectsAKeyThatIsNotASlug(string key)
    {
        var error = Assert.Throws<AuthorizationDomainException>(() => Role.Create(
            RoleId.New(),
            Tenant,
            key,
            "Nombre",
            "Descripcion",
            ["advisorship.read"],
            DateTimeOffset.UnixEpoch));

        Assert.Equal("authorization.role.key_invalid", error.Code);
    }

    [Fact]
    public void CreateRejectsAKeyThatCollidesWithASystemRole()
    {
        // Un rol custom que se llama `admin` deja al catalogo con dos definiciones para la
        // misma clave y a `PermissionsFor` eligiendo una en silencio. La colision se rechaza
        // en el dominio, no se resuelve por precedencia.
        var error = Assert.Throws<AuthorizationDomainException>(() => Role.Create(
            RoleId.New(),
            Tenant,
            "admin",
            "Mi admin",
            "Descripcion",
            ["advisorship.read"],
            DateTimeOffset.UnixEpoch));

        Assert.Equal("authorization.role.key_reserved", error.Code);
    }

    [Fact]
    public void CreateRequiresAtLeastOnePermission()
    {
        var error = Assert.Throws<AuthorizationDomainException>(() => Role.Create(
            RoleId.New(),
            Tenant,
            "vacio",
            "Vacio",
            "Descripcion",
            [],
            DateTimeOffset.UnixEpoch));

        Assert.Equal("authorization.role.permissions_required", error.Code);
    }

    [Fact]
    public void CreateDeduplicatesPermissions()
    {
        var role = ARole("advisorship.read", "advisorship.read", "advisorship.invite");

        Assert.Equal(
            ["advisorship.invite", "advisorship.read"],
            role.Permissions.OrderBy(permission => permission, StringComparer.Ordinal));
    }

    [Fact]
    public void ChangingPermissionsBumpsTheVersion()
    {
        var role = ARole();

        role.ChangePermissions(["advisorship.manage"], DateTimeOffset.UnixEpoch.AddDays(1));

        Assert.Equal(["advisorship.manage"], role.Permissions);
        Assert.Equal(2, role.Version);
    }

    [Fact]
    public void ChangingPermissionsToTheSameSetIsANoOp()
    {
        var role = ARole("advisorship.read", "advisorship.invite");

        role.ChangePermissions(
            ["advisorship.invite", "advisorship.read"],
            DateTimeOffset.UnixEpoch.AddDays(1));

        // Sin esto, abrir el editor y guardar sin tocar nada consume una version y hace que
        // el `If-Match` de quien tenia la pantalla abierta falle sin que nada haya cambiado.
        Assert.Equal(1, role.Version);
    }

    [Fact]
    public void RenamingRejectsAnEmptyDisplayName()
    {
        var role = ARole();

        var error = Assert.Throws<AuthorizationDomainException>(
            () => role.Rename("   ", "Descripcion", DateTimeOffset.UnixEpoch));

        Assert.Equal("authorization.role.display_name_required", error.Code);
    }
}
