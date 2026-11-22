using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Hr.Application;
using Modules.Tenancy.Domain;
using static Modules.Hr.IntegrationTests.HrApiHarness;
using static Modules.Hr.IntegrationTests.HrApiSeeds;

namespace Modules.Hr.IntegrationTests;

/// <summary>
/// Quién ve y quién escribe qué (spec 2026-11-20, D-1): el tenant de la ruta, el módulo, y cada <c>manage</c> con su <c>read</c>. Quien no
/// tiene <c>hr.contract.read</c> ve la ficha enmascarada, con las mismas formas en el <c>GET</c>, la lista y el historial de acciones.
/// </summary>
public sealed class HrAccessApiTests : IClassFixture<HrApiFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly HrApiFixture _fixture;

    public HrAccessApiTests(HrApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task WithoutTheModuleTheListIs403()
    {
        using var tenant = await NewTenantAsync(_fixture, modules: TenantModuleKeys.All.Where(key => key != TenantModuleKeys.Hr).ToList());

        var response = await tenant.Hr.GetAsync($"{tenant.Url}/employees", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // `hr` depende de `companies`: sin Empresas, RRHH queda apagado en la práctica y la lista es 403.
    [Fact]
    public async Task WithoutCompaniesTheModuleIsOff()
    {
        using var tenant = await NewTenantAsync(_fixture, modules: [TenantModuleKeys.Hr]);

        var response = await tenant.Hr.GetAsync($"{tenant.Url}/employees", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AnotherTenantInTheRouteIs403AuthorizationDenied()
    {
        using var tenant = await NewTenantAsync(_fixture);
        using var other = await NewTenantAsync(_fixture);

        var response = await other.Hr.GetAsync($"{tenant.Url}/employees", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("authorization.denied", CodeOf(await JsonOfAsync(response)));
    }

    // D-1: un `manage` sin su `read` no alcanza.
    [Fact]
    public async Task ManageWithoutReadIs403()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var body = HireBody(setup);
        var detail = await HireAsync(tenant, body);
        using var onlyManage = tenant.Client(HrPermissions.EmployeeManage);

        var response = await PutAsync(onlyManage, EmployeeUrl(tenant, detail), UpdateBody(body), Version(detail));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CreatingNeedsTheFourPermissions()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        // Tiene `hr.contract.manage` (la política de la ruta) pero no `hr.employee.manage`.
        using var withoutEmployeeManage = tenant.Client(HrPermissions.EmployeeRead, HrPermissions.ContractRead, HrPermissions.ContractManage);
        // Tiene los tres de empleados pero no `hr.contract.manage`.
        using var withoutContractManage = tenant.Client(HrPermissions.EmployeeRead, HrPermissions.EmployeeManage, HrPermissions.ContractRead);

        var first = await withoutEmployeeManage.PostAsJsonAsync($"{tenant.Url}/employees", HireBody(setup), Ct);
        var second = await withoutContractManage.PostAsJsonAsync($"{tenant.Url}/employees", HireBody(setup), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, first.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, second.StatusCode);
    }

    [Fact]
    public async Task AReaderWithoutContractReadGetsTheDetailMasked()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var detail = await HireAsync(
            tenant,
            HireBody(setup, payment: new { paymentMethod = "BankTransfer", bankName = "Bancolombia", bankAccountType = "Savings", bankAccountNumber = "123" }));
        using var reader = tenant.Client(HrPermissions.EmployeeRead);

        var masked = await GetEmployeeAsync(tenant, detail, reader);
        var list = await JsonOfAsync(await reader.GetAsync($"{tenant.Url}/employees", Ct));

        Assert.False(masked.GetProperty("canReadContract").GetBoolean());
        Assert.Equal(JsonValueKind.Null, masked.GetProperty("employee").GetProperty("payment").ValueKind);
        Assert.Equal(JsonValueKind.Null, masked.GetProperty("contract").ValueKind);
        Assert.Empty(masked.GetProperty("previousContracts").EnumerateArray());
        Assert.Equal("CO", masked.GetProperty("employee").GetProperty("country").GetString());
        var item = Assert.Single(list.GetProperty("items").EnumerateArray());
        foreach (var field in new[] { "company", "position", "site", "contractType", "duration", "endDate", "renewalNoticeDue" })
        {
            Assert.Equal(JsonValueKind.Null, item.GetProperty(field).ValueKind);
        }

        Assert.Equal(JsonValueKind.Null, list.GetProperty("summary").GetProperty("expiringSoon").ValueKind);
    }

    [Fact]
    public async Task ExpiringSoonWithoutContractReadIs403()
    {
        using var tenant = await NewTenantAsync(_fixture);
        using var reader = tenant.Client(HrPermissions.EmployeeRead);

        var response = await reader.GetAsync($"{tenant.Url}/employees?expiringSoon=true", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task FilteringByCompanyWithoutContractReadIs403()
    {
        using var tenant = await NewTenantAsync(_fixture);
        using var reader = tenant.Client(HrPermissions.EmployeeRead);

        var response = await reader.GetAsync($"{tenant.Url}/employees?companyId={Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // El historial sin `hr.contract.read`: sólo el alta, sin los cambios de salario, cargo ni contrato; el pago y el salario ni aparecen.
    [Fact]
    public async Task TheActivityWithoutContractReadHidesCompensation()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var detail = await HireAsync(tenant, HireBody(setup));
        await PutAsync(
            tenant.Hr,
            $"{EmployeeUrl(tenant, detail)}/payment",
            new { paymentMethod = "BankTransfer", bankName = "Bancolombia", bankAccountType = "Savings", bankAccountNumber = "123" },
            Version(detail));
        using var reader = tenant.Client(HrPermissions.EmployeeRead);

        var masked = await JsonOfAsync(await reader.GetAsync($"{EmployeeUrl(tenant, detail)}/activity", Ct));
        var full = await JsonOfAsync(await tenant.Hr.GetAsync($"{EmployeeUrl(tenant, detail)}/activity", Ct));

        Assert.Equal(2, full.GetProperty("total").GetInt32());
        var entry = Assert.Single(masked.GetProperty("items").EnumerateArray());
        Assert.Equal(1, masked.GetProperty("total").GetInt32());
        Assert.Equal("EmployeeCreated", entry.GetProperty("action").GetString());
        var fields = entry.GetProperty("changes").EnumerateArray().Select(change => change.GetProperty("field").GetString()).ToList();
        Assert.Contains("firstName", fields);
        Assert.DoesNotContain("amount", fields);
        Assert.DoesNotContain("position", fields);
        Assert.DoesNotContain("company", fields);
        Assert.DoesNotContain("bankAccountNumber", fields);
    }
}
