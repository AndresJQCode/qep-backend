using Modules.Authorization.Application;
using Modules.Authorization.Domain;

namespace Modules.Authorization.UnitTests;

public sealed class TenantRoleCatalogTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly Guid OtherTenant = Guid.CreateVersion7();

    private static RoleCatalog SystemCatalog() =>
        new(
            [
                new RoleDefinition(
                    SystemRoleKeys.Admin,
                    "Administrador",
                    "Administra el tenant.",
                    "Tenancy",
                    "high",
                    ["advisorship.invite", "advisorship.manage", "advisorship.read"]),
                new RoleDefinition(
                    SystemRoleKeys.Advisor,
                    "Asesora",
                    "Gestiona clientes.",
                    "Tenancy",
                    "medium",
                    ["advisorship.read"]),
            ],
            []);

    private static Role CustomRole(Guid tenantId, string key, params string[] permissions) =>
        Role.Create(
            RoleId.New(),
            tenantId,
            key,
            key,
            string.Empty,
            permissions,
            DateTimeOffset.UnixEpoch);

    private sealed class StubReader(params Role[] roles) : ICustomRoleReader
    {
        public int Reads { get; private set; }

        public Task<IReadOnlyCollection<Role>> ListAsync(
            Guid tenantId,
            CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult<IReadOnlyCollection<Role>>(
                roles.Where(role => role.TenantId == tenantId).ToArray());
        }
    }

    [Fact]
    public async Task PermissionsForUnionsSystemAndCustomRoles()
    {
        var catalog = new TenantRoleCatalog(
            SystemCatalog(),
            new StubReader(CustomRole(Tenant, "ventas-junior", "catalog.product.read")));

        var permissions = await catalog.PermissionsForAsync(
            Tenant,
            [SystemRoleKeys.Advisor, "ventas-junior"],
            TestContext.Current.CancellationToken);

        Assert.Equal(
            ["advisorship.read", "catalog.product.read"],
            permissions.OrderBy(permission => permission, StringComparer.Ordinal));
    }

    [Fact]
    public async Task ACustomRoleOfAnotherTenantGrantsNothing()
    {
        // El fallo que esta clase existe para impedir: si el catalogo no filtrara por tenant,
        // un rol definido en otra organizacion concederia permisos en esta.
        var catalog = new TenantRoleCatalog(
            SystemCatalog(),
            new StubReader(CustomRole(OtherTenant, "ventas-junior", "catalog.product.read")));

        var permissions = await catalog.PermissionsForAsync(
            Tenant,
            ["ventas-junior"],
            TestContext.Current.CancellationToken);

        Assert.Empty(permissions);
    }

    [Fact]
    public async Task AnUnknownRoleGrantsNothingInsteadOfThrowing()
    {
        var catalog = new TenantRoleCatalog(SystemCatalog(), new StubReader());

        var permissions = await catalog.PermissionsForAsync(
            Tenant,
            ["no-existe"],
            TestContext.Current.CancellationToken);

        // Deny por defecto, igual que el catalogo de sistema: un rol retirado deja a su gente
        // sin permisos, no tira 500 en cada request que hagan.
        Assert.Empty(permissions);
    }

    [Fact]
    public async Task ContainsRoleSeesSystemAndCustomRoles()
    {
        var catalog = new TenantRoleCatalog(
            SystemCatalog(),
            new StubReader(CustomRole(Tenant, "ventas-junior", "advisorship.read")));
        var token = TestContext.Current.CancellationToken;

        Assert.True(await catalog.ContainsRoleAsync(Tenant, SystemRoleKeys.Admin, token));
        Assert.True(await catalog.ContainsRoleAsync(Tenant, "ventas-junior", token));
        Assert.False(await catalog.ContainsRoleAsync(Tenant, "no-existe", token));
        Assert.False(await catalog.ContainsRoleAsync(OtherTenant, "ventas-junior", token));
    }

    [Fact]
    public async Task ListRolesMarksWhichOnesAreEditable()
    {
        var catalog = new TenantRoleCatalog(
            SystemCatalog(),
            new StubReader(CustomRole(Tenant, "ventas-junior", "advisorship.read")));

        var roles = await catalog.ListRolesAsync(Tenant, TestContext.Current.CancellationToken);

        // El front necesita distinguirlos: un rol de sistema se muestra pero no se edita, y
        // sin esta bandera tendria que reimplementar la lista de claves reservadas.
        Assert.True(roles.Single(role => role.Role == SystemRoleKeys.Admin).IsSystem);
        Assert.False(roles.Single(role => role.Role == "ventas-junior").IsSystem);
    }

    [Fact]
    public async Task TheCustomRolesAreReadOncePerScopeNoMatterHowManyQuestions()
    {
        var reader = new StubReader(CustomRole(Tenant, "ventas-junior", "advisorship.read"));
        var catalog = new TenantRoleCatalog(SystemCatalog(), reader);
        var token = TestContext.Current.CancellationToken;

        await catalog.PermissionsForAsync(Tenant, ["ventas-junior"], token);
        await catalog.ContainsRoleAsync(Tenant, "ventas-junior", token);
        await catalog.ListRolesAsync(Tenant, token);

        // `ExternalClaimsTransformation` resuelve permisos en CADA request. Sin memoizar por
        // scope, una pantalla que pregunta tres veces son tres consultas por request.
        Assert.Equal(1, reader.Reads);
    }
}
