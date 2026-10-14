using System.Net;
using System.Net.Http.Json;
using Modules.Pipeline.Api;
using Modules.Pipeline.Application;
using static Modules.Pipeline.IntegrationTests.PipelineApiHarness;

namespace Modules.Pipeline.IntegrationTests;

/// <summary>D21–D23 del spec del frontend (2026-10-13) y H43: <c>GET /pipeline/owners</c>, la lista de dueños posibles que el
/// formulario de negocio sólo dibuja. Quién puede ser dueño lo decide el backend: membresía <c>Active</c> del tenant.</summary>
public sealed class PipelineOwnersApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Setup(
        QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, HttpClient Client, Guid OwnerMemberId, string OwnerEmail);

    private static string OwnersUrl(Guid tenantId) => $"{PipelineUrl(tenantId)}/owners";

    private static async Task<Setup> SetupAsync(QepApiFactory factory, string connectionString)
    {
        var tenant = await RegisterTenantAsync(factory);
        await EnablePipelineAsync(connectionString, tenant.TenantId);
        var owner = await OwnerMembershipIdAsync(connectionString, tenant);
        // La membresía del dueño del tenant no tiene DisplayName: su nombre en la lista es el correo del usuario.
        var email = await ScalarAsync<string>(
            connectionString, "SELECT email FROM identity.users WHERE id = @id", ("id", tenant.OwnerUserId));
        return new Setup(factory, connectionString, tenant, CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions), owner, email);
    }

    private static async Task<DealOwnerDto[]> OwnersAsync(Setup setup)
    {
        var response = await setup.Client.GetAsync(OwnersUrl(setup.Tenant.TenantId), Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DealOwnerDto[]>(Ct))!;
    }

    // Review Focus 1, 2 y 3: sólo las Active del tenant, ordenadas por nombre en español sin distinguir mayúsculas, y quien no tiene
    // nombre entra con su correo.
    [Fact]
    public async Task ListsOnlyTheActiveMembersOfTheTenantOrderedByName()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var (zoe, _) = await SeedMemberAsync(connectionString, setup.Tenant.TenantId, "Zoe Ramos");
        var (angela, _) = await SeedMemberAsync(connectionString, setup.Tenant.TenantId, "Ángela Ríos");
        var (martha, _) = await SeedMemberAsync(connectionString, setup.Tenant.TenantId, "martha lópez");
        foreach (var state in new[] { "Suspended", "Removed", "Invited", "Expired" })
        {
            await SeedMemberAsync(connectionString, setup.Tenant.TenantId, $"Inactiva {state}", state: state);
        }

        var other = await SetupAsync(factory, connectionString);
        using var otherClient = other.Client;
        await SeedMemberAsync(connectionString, other.Tenant.TenantId, "Intrusa Activa");

        var owners = await OwnersAsync(setup);

        Assert.Equal([angela, martha, setup.OwnerMemberId, zoe], owners.Select(owner => owner.MemberId));
        Assert.Equal(["Ángela Ríos", "martha lópez", setup.OwnerEmail, "Zoe Ramos"], owners.Select(owner => owner.Name));
    }

    // Review Focus 4: es un miembro válido (AnActiveMemberWithoutNameOrEmailCanOwnADeal), así que la lista lo trae, y nunca con null.
    [Fact]
    public async Task AnActiveMemberWithoutNameOrEmailIsListedWithAPlaceholder()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var (unnamed, _) = await SeedMemberAsync(connectionString, setup.Tenant.TenantId, displayName: null);

        var owners = await OwnersAsync(setup);

        Assert.Equal(DealOwnerDto.UnnamedMemberName, Assert.Single(owners, owner => owner.MemberId == unnamed).Name);
        Assert.Equal(2, owners.Length);
    }

    // Review Focus 5: lo que la lista devuelve se puede usar como dueño al crear (el selector no ofrece lo que el backend rechaza).
    [Fact]
    public async Task EveryListedMemberCanOwnADeal()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        await SeedMemberAsync(connectionString, setup.Tenant.TenantId, "Marta Ruiz");
        await SeedMemberAsync(connectionString, setup.Tenant.TenantId, displayName: null);
        await SeedMemberAsync(connectionString, setup.Tenant.TenantId, "Pedro Gil", state: "Suspended");
        var (customerId, _) = await SeedCustomerAsync(factory, setup.Tenant.TenantId, "Laura");

        var owners = await OwnersAsync(setup);

        Assert.Equal(3, owners.Length);
        foreach (var owner in owners)
        {
            var response = await setup.Client.PostAsJsonAsync(
                $"{PipelineUrl(setup.Tenant.TenantId)}/deals",
                new CreateDealRequest(customerId, "Dotación", null, null, null, owner.MemberId), Ct);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }
    }

    // Review Focus 6: quien sólo lee el embudo no ve la lista (D21), y con la capacidad apagada el código es el del módulo.
    [Fact]
    public async Task WithoutManageTheListIs403AndWithoutTheCapabilityModuleNotEnabled()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        using var reader = CreateClient(factory, setup.Tenant.OwnerUserId, setup.Tenant.TenantId, ReadPermissions);

        var forbidden = await reader.GetAsync(OwnersUrl(setup.Tenant.TenantId), Ct);
        await DisableModuleAsync(connectionString, setup.Tenant.TenantId, "pipeline");
        var disabled = await setup.Client.GetAsync(OwnersUrl(setup.Tenant.TenantId), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, disabled.StatusCode);
        Assert.Equal("tenancy.module_not_enabled", (await ProblemAsync(disabled)).Code);
    }

    // Review Focus 2: la ruta de otro tenant es 403, no 404 ni una lista vacía.
    [Fact]
    public async Task AnotherTenantsRouteIs403()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setupA = await SetupAsync(factory, connectionString);
        var setupB = await SetupAsync(factory, connectionString);
        using var a = setupA.Client;
        using var b = setupB.Client;

        var underForeignRoute = await b.GetAsync(OwnersUrl(setupA.Tenant.TenantId), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, underForeignRoute.StatusCode);
        Assert.Equal("authorization.denied", (await ProblemAsync(underForeignRoute)).Code);
    }
}
