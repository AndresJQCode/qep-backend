using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Payroll.Application;
using Modules.Payroll.Domain;
using static Modules.Payroll.IntegrationTests.PayrollApiHarness;
using static Modules.Payroll.IntegrationTests.PayrollApiSeeds;

namespace Modules.Payroll.IntegrationTests;

/// <summary>
/// Las vacaciones de un empleado por HTTP: leer, previsualizar, registrar, editar y borrar, y lo que se ve de ellas en el período.
/// Todo en diciembre de 2026 con el reloj el día 2: la primera quincena (2026-12-01..15) es la única abierta; así no hace falta
/// cargar los valores legales de 2027 (la clase de liquidación los carga).
/// </summary>
public sealed class PayrollVacationsApiTests : IClassFixture<PayrollApiFixture>
{
    private static readonly DateTimeOffset SecondOfDecember = new(2026, 12, 2, 15, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly PayrollApiFixture _fixture;

    public PayrollVacationsApiTests(PayrollApiFixture fixture) => _fixture = fixture;

    private sealed record Scene(PayrollTenant Tenant, SeededSetup Setup, Guid EmployeeId, Guid PeriodId, Guid ContractId)
    {
        public string PeriodUrl => $"{Tenant.Url}/periods/{PeriodId}";
    }

    // An employee with Saturday and Sunday as rest days, hired in 2025 (or as the test says), and the first half of December open.
    private async Task<Scene> NewSceneAsync(
        string startDate = "2025-01-01", string salaryType = "Monthly", decimal amount = 3_000_000m)
    {
        var tenant = await NewTenantAsync(_fixture);
        var company = await SeedCompanyAsync(tenant, "Comercializadora QEP S.A.S.", "Principal", "Bodega", riskClass: "I");
        var area = await CreatedAsync(tenant.Payroll, $"{tenant.HrBase}/areas", new { name = "Operaciones" });
        var position = await CreatedAsync(tenant.Payroll, $"{tenant.HrBase}/positions", new { name = "Analista" });
        var setup = new SeededSetup(
            company.Company, company.SiteId, company.WorkplaceId, area.GetProperty("id").GetGuid(), position.GetProperty("id").GetGuid());
        var employee = await HireAsync(
            tenant,
            HireBody(
                setup, startDate: startDate, salaryType: salaryType, amount: amount, restDays: ["Saturday", "Sunday"]));

        _fixture.Clock.UtcNow = SecondOfDecember;
        var ensured = await tenant.Payroll.PostAsync($"{tenant.Url}/companies/{setup.Company.Id}/periods/ensure-current", null, Ct);
        Assert.True(ensured.StatusCode == HttpStatusCode.OK, await BodyAsync(ensured));
        var periodId = (await JsonOfAsync(ensured)).GetProperty("items")[0].GetProperty("id").GetGuid();
        return new Scene(tenant, setup, EmployeeIdOf(employee), periodId, employee.GetProperty("contract").GetProperty("id").GetGuid());
    }

    // Hr's terminate is what sets the real end of the contract (a fixed-term end date is only the planned one).
    private static async Task TerminateAsync(Scene scene, string date)
    {
        var terminate = await scene.Tenant.Payroll.PostAsJsonAsync(
            $"{scene.Tenant.HrBase}/employees/{scene.EmployeeId}/contracts/{scene.ContractId}/terminate",
            new { reason = "Resignation", date, notes = (string?)null },
            Ct);
        Assert.True(terminate.StatusCode == HttpStatusCode.OK, await BodyAsync(terminate));
    }

    private static string VacationsUrl(Scene scene, Guid? employeeId = null, Guid? companyId = null) =>
        $"{scene.Tenant.Url}/companies/{companyId ?? scene.Setup.Company.Id}/employees/{employeeId ?? scene.EmployeeId}/vacations";

    private static object PreviewBody(string origin, string start, string end) => new { origin, startDate = start, endDate = end };

    private static async Task<JsonElement> PreviewAsync(Scene scene, string origin, string start, string end)
    {
        var response = await scene.Tenant.Payroll.PostAsJsonAsync(
            $"{VacationsUrl(scene)}/preview", PreviewBody(origin, start, end), Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await BodyAsync(response));
        return await JsonOfAsync(response);
    }

    private static async Task<JsonElement> PreviewRejectedAsync(Scene scene, string origin, string start, string end)
    {
        var response = await scene.Tenant.Payroll.PostAsJsonAsync(
            $"{VacationsUrl(scene)}/preview", PreviewBody(origin, start, end), Ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        return await JsonOfAsync(response);
    }

    [Fact]
    public async Task PreviewCountsTheBusinessDays()
    {
        var scene = await NewSceneAsync();

        var preview = await PreviewAsync(scene, "Qep", "2026-12-14", "2027-01-05");

        // Saturdays and Sundays are rest days and the 25th of December and the 1st of January are holidays.
        Assert.Equal(15, preview.GetProperty("businessDays").GetInt32());
        Assert.Equal("2027-01-06", preview.GetProperty("returnDate").GetString());
        Assert.Equal(
            [("2026-12-01", "2026-12-15", 2), ("2026-12-16", "2026-12-31", 15), ("2027-01-01", "2027-01-15", 5)],
            preview.GetProperty("quincenas").EnumerateArray().Select(item => (
                item.GetProperty("startDate").GetString()!, item.GetProperty("endDate").GetString()!, item.GetProperty("days").GetInt32())));
        Assert.Equal(["2026-12", "2027-01"], preview.GetProperty("months").EnumerateArray().Select(item => item.GetString()));
        var paidIn = preview.GetProperty("paidIn");
        Assert.Equal(("2026-12-01", "2026-12-15"), (paidIn.GetProperty("startDate").GetString(), paidIn.GetProperty("endDate").GetString()));
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task PreviewOfExternalHasNoPaidIn()
    {
        var scene = await NewSceneAsync();

        var preview = await PreviewAsync(scene, "External", "2026-12-14", "2027-01-05");

        Assert.Equal(JsonValueKind.Null, preview.GetProperty("paidIn").ValueKind);
        Assert.Equal(15, preview.GetProperty("businessDays").GetInt32());
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task PreviewRejectsARestDayStart()
    {
        var scene = await NewSceneAsync();

        // 2026-12-12 is a Saturday: a rest day of this employee.
        var problem = await PreviewRejectedAsync(scene, "Qep", "2026-12-12", "2026-12-24");

        Assert.Equal("payroll.vacation.starts_on_rest_day", CodeOf(problem));
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task PreviewRejectsAHolidayStart()
    {
        var scene = await NewSceneAsync();

        var problem = await PreviewRejectedAsync(scene, "Qep", "2026-12-25", "2026-12-30");

        Assert.Equal("payroll.vacation.starts_on_rest_day", CodeOf(problem));
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task PreviewRejectsOutsideTheContract()
    {
        var scene = await NewSceneAsync();
        await TerminateAsync(scene, "2026-12-31");

        var problem = await PreviewRejectedAsync(scene, "Qep", "2026-12-14", "2027-01-05");

        Assert.Equal("payroll.vacation.outside_contract", CodeOf(problem));
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task PreviewRejectsBeforeTheFirstPeriod()
    {
        var scene = await NewSceneAsync();

        // The first quincena of the company is 2026-12-01..15: a Qep vacation that starts before it was paid elsewhere.
        var problem = await PreviewRejectedAsync(scene, "Qep", "2026-11-16", "2026-11-27");

        Assert.Equal("payroll.vacation.before_first_period", CodeOf(problem));
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task PreviewOfExternalMayStartBeforeTheFirstPeriodButNotEndBeforeIt()
    {
        var scene = await NewSceneAsync();

        var preview = await PreviewAsync(scene, "External", "2026-11-23", "2026-12-04");
        var problem = await PreviewRejectedAsync(scene, "External", "2026-11-16", "2026-11-27");

        Assert.Equal(["2026-11", "2026-12"], preview.GetProperty("months").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal("payroll.vacation.before_first_period", CodeOf(problem));
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task PreviewRejectsAnEmployeeThePeriodExcludes()
    {
        // An integral salary is outside what the engine calculates: the quincena that would pay the vacation excludes the employee.
        var scene = await NewSceneAsync(salaryType: "Integral", amount: 25_000_000m);

        var problem = await PreviewRejectedAsync(scene, "Qep", "2026-12-14", "2026-12-24");

        Assert.Equal("payroll.employee.not_liquidable", CodeOf(problem));
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task ListIsEmptyAtFirst()
    {
        var scene = await NewSceneAsync();

        var response = await scene.Tenant.Payroll.GetAsync(VacationsUrl(scene), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, (await JsonOfAsync(response)).GetArrayLength());
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task ListLeavesOutThoseOfAnotherCompanyOfTheTenant()
    {
        var scene = await NewSceneAsync();
        var other = await SeedSecondCompanyAsync(scene.Tenant);
        await SaveVacationRowAsync(scene.Tenant.Id, other.Company.Id, scene.EmployeeId, new DateOnly(2026, 12, 14), new DateOnly(2026, 12, 24));

        var response = await scene.Tenant.Payroll.GetAsync(VacationsUrl(scene), Ct);

        Assert.Equal(0, (await JsonOfAsync(response)).GetArrayLength());
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task ReadingNeedsPeriodRead()
    {
        var scene = await NewSceneAsync();
        using var manager = scene.Tenant.Client(PayrollPermissions.PeriodManage);
        using var settings = scene.Tenant.Client(PayrollPermissions.SettingsManage);

        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync(VacationsUrl(scene), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await settings.GetAsync(VacationsUrl(scene), Ct)).StatusCode);
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task PreviewingNeedsReadAndManage()
    {
        var scene = await NewSceneAsync();
        using var reader = scene.Tenant.Client(PayrollPermissions.PeriodRead);

        var response = await reader.PostAsJsonAsync($"{VacationsUrl(scene)}/preview", PreviewBody("Qep", "2026-12-14", "2026-12-24"), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task AnotherTenantsEmployeeIs404()
    {
        var scene = await NewSceneAsync();
        var foreign = await NewSceneAsync();

        var list = await scene.Tenant.Payroll.GetAsync(VacationsUrl(scene, employeeId: foreign.EmployeeId), Ct);
        var preview = await scene.Tenant.Payroll.PostAsJsonAsync(
            $"{VacationsUrl(scene, employeeId: foreign.EmployeeId)}/preview", PreviewBody("Qep", "2026-12-14", "2026-12-24"), Ct);

        Assert.Equal(HttpStatusCode.NotFound, list.StatusCode);
        Assert.Equal("payroll.employee.not_found", CodeOf(await JsonOfAsync(list)));
        Assert.Equal(HttpStatusCode.NotFound, preview.StatusCode);
        scene.Tenant.Dispose();
        foreign.Tenant.Dispose();
    }

    [Fact]
    public async Task AnInvalidPreviewBodyIs422WithTheField()
    {
        var scene = await NewSceneAsync();

        var response = await scene.Tenant.Payroll.PostAsJsonAsync(
            $"{VacationsUrl(scene)}/preview", new { origin = "Other", startDate = "2026-12-14" }, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await JsonOfAsync(response);
        Assert.Equal("validation.failed", CodeOf(problem));
        Assert.True(HasError(problem, "origin"));
        Assert.True(HasError(problem, "endDate"));
        scene.Tenant.Dispose();
    }

    // A row written straight through the repository: the way to have a vacation of another company or employee without an Hr flow.
    private async Task SaveVacationRowAsync(Guid tenantId, Guid companyId, Guid employeeId, DateOnly start, DateOnly end)
    {
        await using var scope = _fixture.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IVacationRepository>().Add(Vacation.Create(
            VacationId.New(), tenantId, companyId, employeeId, VacationOrigin.Qep, start, end, 8, end.AddDays(3), null, [],
            PayrollApiFixture.DefaultNow));
        await scope.ServiceProvider.GetRequiredService<IPayrollUnitOfWork>().SaveChangesAsync(Ct);
    }
}
