using BuildingBlocks.Application;
using Modules.Integrations.Application;
using Modules.Integrations.Application.Shopify;
using Modules.Integrations.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Integrations.UnitTests;

/// <summary>
/// Spec 2026-10-15 §4.3 paso 5 y §12: <c>GET /installs/{installId}</c> sólo pide autenticación. Responde 404 para un id
/// desconocido, vencido o todavía sin canjear, y lista <b>todos</b> los tenants activos del usuario con <c>canLink</c>
/// (tiene <c>integrations.connection.manage</c> en ese tenant) y <c>moduleEnabled</c> (tiene <c>shopify</c>).
/// </summary>
public sealed class GetShopifyInstallHandlerTests
{
    private const string Shop = "mi-tienda.myshopify.com";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Bed
    {
        public IntegrationsTestBed Integrations { get; } = new();

        public InMemoryShopifyInstallRepository Installs { get; } = new();

        public FakeActiveTenantsQuery ActiveTenants { get; } = new();

        public FakeRolePermissionChecker Roles { get; } = new();

        public GetShopifyInstallHandler Handler() =>
            new(Installs, ActiveTenants, Roles, Integrations.Modules, Integrations.Context(), Integrations.Clock);

        public ShopifyInstall Seed(bool exchange = true, Guid? tenantId = null, Guid? memberId = null)
        {
            var install = ShopifyInstall.Start(
                Guid.NewGuid(), Shop, $"state-{Guid.NewGuid():N}", "https://qep.qcode.co/shopify/callback", tenantId, memberId, IntegrationsTestBed.Now);
            if (exchange)
            {
                install.MarkExchanged("12345678", "Mi Tienda", "read_orders,read_customers", new ProtectedSecret("test", [1]), IntegrationsTestBed.Now);
            }

            Installs.Add(install);
            return install;
        }
    }

    [Fact]
    public async Task GetUnknownThrowsInstallNotFound()
    {
        var bed = new Bed();

        var error = await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            bed.Handler().HandleAsync(new GetShopifyInstallQuery(Guid.NewGuid()), Ct));

        Assert.Equal(IntegrationsErrorCodes.ShopifyInstallNotFound, error.Code);
    }

    [Fact]
    public async Task GetExpiredThrowsInstallNotFound()
    {
        var bed = new Bed();
        var install = bed.Seed();
        bed.Integrations.Clock.UtcNow = install.ExpiresAt;

        var error = await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            bed.Handler().HandleAsync(new GetShopifyInstallQuery(install.Id), Ct));

        Assert.Equal(IntegrationsErrorCodes.ShopifyInstallNotFound, error.Code);
    }

    // Sin canje no hay tienda que mostrar, y el installId sólo lo revela el canje: para quien pregunta, no existe.
    [Fact]
    public async Task GetNotExchangedThrowsInstallNotFound()
    {
        var bed = new Bed();
        var install = bed.Seed(exchange: false);

        await Assert.ThrowsAsync<ResourceNotFoundException>(() => bed.Handler().HandleAsync(new GetShopifyInstallQuery(install.Id), Ct));
    }

    [Fact]
    public async Task GetListsEveryActiveTenantWithBothFlags()
    {
        var bed = new Bed();
        var install = bed.Seed(tenantId: bed.Integrations.TenantId, memberId: bed.Integrations.MemberId);
        var advisorTenant = Guid.NewGuid();
        var moduleOffTenant = Guid.NewGuid();
        bed.ActiveTenants.Tenants.AddRange(
        [
            new ActiveTenantSummary(bed.Integrations.TenantId, "Acme", ["admin"]),
            new ActiveTenantSummary(advisorTenant, "Beta", ["advisor"]),
            new ActiveTenantSummary(moduleOffTenant, "Gamma", ["admin"]),
        ]);
        bed.Roles.Granting["admin"] = IntegrationsPermissions.ConnectionManage;
        bed.Integrations.Modules.Sets[moduleOffTenant] = TenantModuleSet.FromStored(TenantModuleKeys.All.Except([TenantModuleKeys.Shopify]));
        bed.Integrations.Modules.Sets[advisorTenant] = TenantModuleSet.FromStored(TenantModuleKeys.All);

        var response = await bed.Handler().HandleAsync(new GetShopifyInstallQuery(install.Id), Ct);

        Assert.Equal(Shop, response.ShopDomain);
        Assert.Equal("Mi Tienda", response.ShopName);
        Assert.Equal(install.ExpiresAt, response.ExpiresAt);
        Assert.Equal(bed.Integrations.TenantId, response.InitiatedTenantId);
        Assert.Equal(
            [
                new ShopifyInstallTenantResponse(bed.Integrations.TenantId, "Acme", CanLink: true, ModuleEnabled: true),
                new ShopifyInstallTenantResponse(advisorTenant, "Beta", CanLink: false, ModuleEnabled: true),
                new ShopifyInstallTenantResponse(moduleOffTenant, "Gamma", CanLink: true, ModuleEnabled: false),
            ],
            response.Tenants);
        Assert.Equal(bed.Integrations.SubjectId, bed.ActiveTenants.LastUserId);
    }

    // Sin tenants: la pantalla ofrece /register; la lista viaja vacía, no ausente.
    [Fact]
    public async Task GetWithoutTenantsReturnsAnEmptyList()
    {
        var bed = new Bed();
        var install = bed.Seed();

        var response = await bed.Handler().HandleAsync(new GetShopifyInstallQuery(install.Id), Ct);

        Assert.Empty(response.Tenants);
    }
}

internal sealed class FakeActiveTenantsQuery : IActiveTenantsQuery
{
    public List<ActiveTenantSummary> Tenants { get; } = [];

    public Guid? LastUserId { get; private set; }

    public Task<IReadOnlyCollection<ActiveTenantSummary>> ListActiveTenantsAsync(Guid userId, CancellationToken cancellationToken)
    {
        LastUserId = userId;
        return Task.FromResult<IReadOnlyCollection<ActiveTenantSummary>>(Tenants);
    }
}

/// <summary>Rol → el único permiso que concede, sin importar el tenant: basta para las banderas.</summary>
internal sealed class FakeRolePermissionChecker : IRolePermissionChecker
{
    public Dictionary<string, string> Granting { get; } = new(StringComparer.Ordinal);

    public Task<bool> AnyGrantsAsync(TenantId tenantId, IReadOnlyCollection<string> roles, string permission, CancellationToken cancellationToken) =>
        Task.FromResult(roles.Any(role => Granting.TryGetValue(role, out var granted) && string.Equals(granted, permission, StringComparison.Ordinal)));
}
