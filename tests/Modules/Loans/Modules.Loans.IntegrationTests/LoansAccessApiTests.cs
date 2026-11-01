using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Loans.Application;
using Modules.Tenancy.Domain;
using static Modules.Loans.IntegrationTests.LoansApiHarness;

namespace Modules.Loans.IntegrationTests;

/// <summary>
/// El acceso al módulo de préstamos (spec 2026-10-31), de punta a punta por HTTP, sobre <c>GET /assignees</c>: la política, el
/// tenant de la ruta, el módulo habilitado y quién puede ser encargado.
/// </summary>
public sealed class LoansAccessApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string AssigneesUrl(string tenantId = LoansApiHarness.TenantId) => $"{LoansUrl(tenantId)}/assignees";

    private static async Task<string?> CodeOfAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await BodyAsync(response));
        return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    [Fact]
    public async Task WithoutThePermissionTheAssigneesAre403()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        // Con permisos de otro módulo, pero sin loans.loan.read: la política frena antes del handler.
        using var client = CreateClient(factory, SubjectId, LoansApiHarness.TenantId, "tenancy.settings.read");

        var response = await client.GetAsync(AssigneesUrl(), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AnotherTenantInTheRouteIs403Never404()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        await SeedTenantAsync(factory, OtherTenantId);
        // Quien opera en el otro tenant y apunta a la ruta de éste: 403, nunca 404.
        using var outsider = CreateClient(factory, OtherSubjectId, OtherTenantId, LoansPermissions.Read, LoansPermissions.Manage);

        var response = await outsider.GetAsync(AssigneesUrl(), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("authorization.denied", await CodeOfAsync(response));
    }

    [Fact]
    public async Task AnotherTenantInTheRouteIs403OnAWriteRouteToo()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        await SeedTenantAsync(factory, OtherTenantId);
        using var outsider = CreateClient(factory, OtherSubjectId, OtherTenantId, LoansPermissions.Read, LoansPermissions.Manage);
        var body = new Dictionary<string, object?>
        {
            ["customerId"] = Guid.NewGuid(),
            ["name"] = "Intruso",
            ["principalAmount"] = 1_000m,
            ["currency"] = null,
            ["monthlyRatePercent"] = 1.5m,
            ["disbursedOn"] = "2026-10-01",
            ["cutoffRule"] = "EndOfMonth",
            ["assigneeMemberId"] = null,
            ["note"] = null,
        };

        var response = await outsider.PostAsJsonAsync(LoansUrl(), body, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("authorization.denied", await CodeOfAsync(response));
    }

    [Fact]
    public async Task WithTheModuleOffTheRouteIs403()
    {
        var (database, factory) = await OpenAsync(TenantModuleKeys.All.Where(key => key != TenantModuleKeys.Loans));
        await using var _ = database;
        using var __ = factory;
        using var manager = CreateManager(factory);

        var response = await manager.GetAsync(AssigneesUrl(), Ct);

        // ModuleEntitlementMask quita el permiso antes del handler: 403 pelado, sin código de módulo.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain("tenancy.module_not_enabled", await BodyAsync(response));
    }

    [Fact]
    public async Task TheAssigneesAreTheActiveMembersWhoCanManage()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var zoe = await SeedMemberAsync(factory, "zoe@example.com", "Zoe Admin", "admin");
        var bea = await SeedMemberAsync(factory, "bea@example.com", "Bea Facturación", "billing");
        await SeedMemberAsync(factory, "caro@example.com", "Caro Asesora", "advisor");
        await SeedMemberAsync(factory, "rita@example.com", "Rita Quitada", "admin", removed: true);
        using var reader = CreateReader(factory);

        var response = await reader.GetAsync(AssigneesUrl(), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var assignees = await response.Content.ReadFromJsonAsync<LoanAssigneesDto>(Ct);
        Assert.Equal(
            [new LoanMemberDto(bea, "Bea Facturación", true), new LoanMemberDto(zoe, "Zoe Admin", true)],
            assignees!.Items);
    }
}
