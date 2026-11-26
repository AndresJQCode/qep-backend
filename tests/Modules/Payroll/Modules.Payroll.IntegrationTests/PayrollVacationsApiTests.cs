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
    public async Task PreviewOfAVacationAtTheEndOfTimeDoesNotFail500()
    {
        var scene = await NewSceneAsync();

        var response = await scene.Tenant.Payroll.PostAsJsonAsync(
            $"{VacationsUrl(scene)}/preview", PreviewBody("Qep", "9999-12-31", "9999-12-31"), Ct);

        Assert.True((int)response.StatusCode < 500, await BodyAsync(response));
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task PreviewRejectsAVacationLongerThan366Days()
    {
        var scene = await NewSceneAsync();

        var problem = await PreviewRejectedAsync(scene, "Qep", "2026-12-14", "9999-12-31");

        Assert.Equal("validation.failed", CodeOf(problem));
        Assert.True(HasError(problem, "endDate"));
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

    [Fact]
    public async Task PreviewReturnDateSkipsTheHolidaysAfterTheEnd()
    {
        var scene = await NewSceneAsync();

        // The 25th is a holiday and the 26th and 27th are the weekend: the employee is back on Monday the 28th.
        var preview = await PreviewAsync(scene, "Qep", "2026-12-21", "2026-12-24");

        Assert.Equal("2026-12-28", preview.GetProperty("returnDate").GetString());
        scene.Tenant.Dispose();
    }

    // --- Writing ---

    private static object QepBody(string start, string end, decimal? previousIbc = null) =>
        new { origin = "Qep", startDate = start, endDate = end, previousMonthIbc = previousIbc };

    private static object ExternalBody(string start, string end, int businessDays, object[]? months = null, decimal previousIbc = 2_800_000m) =>
        new { origin = "External", startDate = start, endDate = end, previousMonthIbc = previousIbc, businessDays, externalMonths = months ?? [] };

    private static async Task<JsonElement> CreateAsync(Scene scene, object body, Guid? employeeId = null)
    {
        var response = await scene.Tenant.Payroll.PostAsJsonAsync(VacationsUrl(scene, employeeId), body, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await BodyAsync(response));
        return await JsonOfAsync(response);
    }

    private static Task<HttpResponseMessage> UpdateAsync(Scene scene, Guid vacationId, object body, string? ifMatch, Guid? employeeId = null) =>
        PutAsync(scene.Tenant.Payroll, $"{VacationsUrl(scene, employeeId)}/{vacationId}", body, ifMatch);

    private static async Task<HttpResponseMessage> DeleteAsync(Scene scene, Guid vacationId, string? ifMatch, Guid? employeeId = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"{VacationsUrl(scene, employeeId)}/{vacationId}");
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return await scene.Tenant.Payroll.SendAsync(request, Ct);
    }

    private static string Etag(JsonElement vacation) => $"\"{vacation.GetProperty("version").GetInt64()}\"";

    private static async Task<JsonElement> OkAsync(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode == HttpStatusCode.OK, await BodyAsync(response));
        return await JsonOfAsync(response);
    }

    private static async Task<JsonElement> PeriodAsync(Scene scene)
    {
        var response = await scene.Tenant.Payroll.GetAsync(scene.PeriodUrl, Ct);
        return (await OkAsync(response)).GetProperty("period");
    }

    private static async Task<JsonElement> TransitionAsync(Scene scene, string action, object? body = null)
    {
        var version = (await PeriodAsync(scene)).GetProperty("version").GetInt64();
        return await OkAsync(await PostAsync(scene.Tenant.Payroll, $"{scene.PeriodUrl}/{action}", $"\"{version}\"", body));
    }

    private static Task<JsonElement> LiquidatedAsync(Scene scene) => TransitionAsync(scene, "liquidate", new { confirmNoAbsences = true });

    private static Task<JsonElement> ApprovedAsync(Scene scene) => TransitionAsync(scene, "approve");

    private static JsonElement Single(JsonElement list) => Assert.Single(list.EnumerateArray());

    [Fact]
    public async Task CreateAnswersTheWholeList()
    {
        var scene = await NewSceneAsync();

        var list = await CreateAsync(scene, QepBody("2026-12-14", "2027-01-05"));

        var vacation = Single(list);
        Assert.Equal(15, vacation.GetProperty("businessDays").GetInt32());
        Assert.Equal("2027-01-06", vacation.GetProperty("returnDate").GetString());
        Assert.Equal("Qep", vacation.GetProperty("origin").GetString());
        Assert.Equal(scene.EmployeeId, vacation.GetProperty("employeeId").GetGuid());
        var paidIn = vacation.GetProperty("paidIn");
        Assert.Equal(("2026-12-01", "2026-12-15", scene.PeriodId, "Draft"), (
            paidIn.GetProperty("startDate").GetString(), paidIn.GetProperty("endDate").GetString(),
            paidIn.GetProperty("periodId").GetGuid(), paidIn.GetProperty("status").GetString()));
        Assert.Equal([2, 15, 5], vacation.GetProperty("quincenas").EnumerateArray().Select(item => item.GetProperty("days").GetInt32()));
        Assert.Equal(0, vacation.GetProperty("externalMonths").GetArrayLength());
        Assert.True(vacation.GetProperty("can").GetProperty("update").GetBoolean());
        Assert.True(vacation.GetProperty("can").GetProperty("delete").GetBoolean());
        Assert.Equal(1, vacation.GetProperty("version").GetInt64());
        Assert.Equal(
            1, await CountAuditEntriesAsync(_fixture.Factory, PayrollAuditActions.VacationCreated, vacation.GetProperty("id").GetString()!));
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task TheListComesMostRecentFirst()
    {
        var scene = await NewSceneAsync();
        await CreateAsync(scene, QepBody("2026-12-02", "2026-12-04"));

        var list = await CreateAsync(scene, QepBody("2026-12-14", "2026-12-18"));

        Assert.Equal(
            ["2026-12-14", "2026-12-02"], list.EnumerateArray().Select(item => item.GetProperty("startDate").GetString()));
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task ExternalKeepsWhatWasTyped()
    {
        var scene = await NewSceneAsync();

        // QEP counts 10 business days from the 23rd of November to the 4th of December; Siigo paid 9 and that is what is kept.
        var list = await CreateAsync(scene, ExternalBody(
            "2026-11-23",
            "2026-12-04",
            businessDays: 9,
            months:
            [
                new { month = "2026-12", healthAdvance = 19_200m },
                new { month = "2026-11", vacationPay = 320_000m, healthAdvance = 12_800m, pensionAdvance = 12_800m },
            ]));

        var vacation = Single(list);
        Assert.Equal(9, vacation.GetProperty("businessDays").GetInt32());
        Assert.Equal(JsonValueKind.Null, vacation.GetProperty("paidIn").ValueKind);
        Assert.Equal(2_800_000m, vacation.GetProperty("previousMonthIbc").GetDecimal());
        var months = vacation.GetProperty("externalMonths").EnumerateArray().ToList();
        Assert.Equal(["2026-11", "2026-12"], months.Select(item => item.GetProperty("month").GetString()));
        Assert.Equal(
            (320_000m, 12_800m, 12_800m, 0m),
            (months[0].GetProperty("vacationPay").GetDecimal(), months[0].GetProperty("healthAdvance").GetDecimal(),
                months[0].GetProperty("pensionAdvance").GetDecimal(), months[0].GetProperty("solidarityFundAdvance").GetDecimal()));
        Assert.Equal(JsonValueKind.Null, months[1].GetProperty("vacationPay").ValueKind);
        Assert.Equal(
            (19_200m, 0m, 0m),
            (months[1].GetProperty("healthAdvance").GetDecimal(), months[1].GetProperty("pensionAdvance").GetDecimal(),
                months[1].GetProperty("solidarityFundAdvance").GetDecimal()));
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task OverlapIs409()
    {
        var scene = await NewSceneAsync();
        await CreateAsync(scene, QepBody("2026-12-14", "2026-12-24"));

        var response = await scene.Tenant.Payroll.PostAsJsonAsync(VacationsUrl(scene), QepBody("2026-12-22", "2026-12-30"), Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("payroll.vacation.overlap", CodeOf(await JsonOfAsync(response)));
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task UpdateNeedsIfMatch()
    {
        var scene = await NewSceneAsync();
        var vacation = Single(await CreateAsync(scene, QepBody("2026-12-14", "2026-12-24")));

        var response = await UpdateAsync(scene, vacation.GetProperty("id").GetGuid(), QepBody("2026-12-14", "2026-12-23"), ifMatch: null);

        Assert.Equal((HttpStatusCode)428, response.StatusCode);
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task UpdateWithAStaleVersionIs412()
    {
        var scene = await NewSceneAsync();
        var vacation = Single(await CreateAsync(scene, QepBody("2026-12-14", "2026-12-24")));
        var id = vacation.GetProperty("id").GetGuid();
        await OkAsync(await UpdateAsync(scene, id, QepBody("2026-12-14", "2026-12-23"), Etag(vacation)));

        var stale = await UpdateAsync(scene, id, QepBody("2026-12-14", "2026-12-22"), Etag(vacation));

        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task UpdateChangesTheVacationAndAnswersTheList()
    {
        var scene = await NewSceneAsync();
        var vacation = Single(await CreateAsync(scene, QepBody("2026-12-14", "2026-12-24")));
        var id = vacation.GetProperty("id").GetGuid();

        var list = await OkAsync(await UpdateAsync(scene, id, QepBody("2026-12-14", "2026-12-22", previousIbc: 2_900_000m), Etag(vacation)));

        var updated = Single(list);
        Assert.Equal(("2026-12-22", 2, 2_900_000m), (
            updated.GetProperty("endDate").GetString(), updated.GetProperty("version").GetInt64(), updated.GetProperty("previousMonthIbc").GetDecimal()));
        Assert.Equal(1, await CountAuditEntriesAsync(_fixture.Factory, PayrollAuditActions.VacationUpdated, id.ToString()));
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task UpdateOfAnotherEmployeesVacationIs404()
    {
        var scene = await NewSceneAsync();
        var other = EmployeeIdOf(await HireAsync(
            scene.Tenant, HireBody(scene.Setup, documentNumber: "2020304050", firstName: "Luis", restDays: ["Sunday"])));
        var theirs = Single(await CreateAsync(scene, QepBody("2026-12-14", "2026-12-24"), employeeId: other));
        var id = theirs.GetProperty("id").GetGuid();

        var update = await UpdateAsync(scene, id, QepBody("2026-12-14", "2026-12-23"), Etag(theirs));
        var delete = await DeleteAsync(scene, id, Etag(theirs));

        Assert.Equal(HttpStatusCode.NotFound, update.StatusCode);
        Assert.Equal("payroll.vacation.not_found", CodeOf(await JsonOfAsync(update)));
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
        Assert.Equal("payroll.vacation.not_found", CodeOf(await JsonOfAsync(delete)));
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task UpdateOfAnotherCompanysVacationIs404()
    {
        var scene = await NewSceneAsync();
        var otherCompany = await SeedSecondCompanyAsync(scene.Tenant);
        await SaveVacationRowAsync(scene.Tenant.Id, otherCompany.Company.Id, scene.EmployeeId, new DateOnly(2026, 12, 14), new DateOnly(2026, 12, 24));
        Guid id;
        await using (var scope = _fixture.Factory.Services.CreateAsyncScope())
        {
            id = (await scope.ServiceProvider.GetRequiredService<IVacationRepository>()
                .ListByEmployeeAsync(scene.Tenant.Id, otherCompany.Company.Id, scene.EmployeeId, Ct)).Single().Id.Value;
        }

        var update = await UpdateAsync(scene, id, QepBody("2026-12-14", "2026-12-23"), "\"1\"");
        var delete = await DeleteAsync(scene, id, "\"1\"");

        Assert.Equal(HttpStatusCode.NotFound, update.StatusCode);
        Assert.Equal("payroll.vacation.not_found", CodeOf(await JsonOfAsync(update)));
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task DeleteAnswersTheRemainingList()
    {
        var scene = await NewSceneAsync();
        await CreateAsync(scene, QepBody("2026-12-02", "2026-12-04"));
        var both = await CreateAsync(scene, QepBody("2026-12-14", "2026-12-18"));
        var first = both.EnumerateArray().Single(item => item.GetProperty("startDate").GetString() == "2026-12-02");

        var rest = await OkAsync(await DeleteAsync(scene, first.GetProperty("id").GetGuid(), Etag(first)));

        Assert.Equal("2026-12-14", Single(rest).GetProperty("startDate").GetString());
        Assert.Equal(
            1, await CountAuditEntriesAsync(_fixture.Factory, PayrollAuditActions.VacationDeleted, first.GetProperty("id").GetString()!));
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task DeleteNeedsIfMatch()
    {
        var scene = await NewSceneAsync();
        var vacation = Single(await CreateAsync(scene, QepBody("2026-12-14", "2026-12-24")));

        var response = await DeleteAsync(scene, vacation.GetProperty("id").GetGuid(), ifMatch: null);

        Assert.Equal((HttpStatusCode)428, response.StatusCode);
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task ALiquidatedQuincenaItReachesGetsOutdated()
    {
        var scene = await NewSceneAsync();
        var liquidated = await LiquidatedAsync(scene);
        var before = liquidated.GetProperty("period").GetProperty("version").GetInt64();

        await CreateAsync(scene, QepBody("2026-12-14", "2026-12-24"));

        var period = await PeriodAsync(scene);
        Assert.True(period.GetProperty("outdated").GetBoolean());
        Assert.Equal(before + 1, period.GetProperty("version").GetInt64());
        scene.Tenant.Dispose();
    }

    // A reopened quincena is editable but its liquidation is stale the same way: spec «desactualizar Liquidadas y Reabiertas».
    private static async Task<(JsonElement Vacation, JsonElement Reopened)> ReopenedWithAVacationAsync(Scene scene)
    {
        var vacation = Single(await CreateAsync(scene, QepBody("2026-12-14", "2026-12-24")));
        await LiquidatedAsync(scene);
        await ApprovedAsync(scene);
        await TransitionAsync(scene, "reopen", new { reason = "Corregir" });
        var reopened = await PeriodAsync(scene);
        Assert.Equal("Reopened", reopened.GetProperty("status").GetString());
        Assert.False(reopened.GetProperty("outdated").GetBoolean());
        return (vacation, reopened);
    }

    [Fact]
    public async Task EditingAVacationOutdatesAReopenedQuincenaItReaches()
    {
        var scene = await NewSceneAsync();
        var (vacation, _) = await ReopenedWithAVacationAsync(scene);

        await OkAsync(await UpdateAsync(scene, vacation.GetProperty("id").GetGuid(), QepBody("2026-12-14", "2026-12-23"), Etag(vacation)));

        var period = await PeriodAsync(scene);
        Assert.True(period.GetProperty("outdated").GetBoolean());
        Assert.Equal("Reopened", period.GetProperty("status").GetString());
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task DeletingAVacationOutdatesAReopenedQuincenaItReaches()
    {
        var scene = await NewSceneAsync();
        var (vacation, _) = await ReopenedWithAVacationAsync(scene);

        await OkAsync(await DeleteAsync(scene, vacation.GetProperty("id").GetGuid(), Etag(vacation)));

        var period = await PeriodAsync(scene);
        Assert.True(period.GetProperty("outdated").GetBoolean());
        Assert.Equal("Reopened", period.GetProperty("status").GetString());
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task ADraftIsNotTouched()
    {
        var scene = await NewSceneAsync();
        var before = (await PeriodAsync(scene)).GetProperty("version").GetInt64();

        await CreateAsync(scene, QepBody("2026-12-14", "2026-12-24"));

        var period = await PeriodAsync(scene);
        Assert.False(period.GetProperty("outdated").GetBoolean());
        Assert.Equal(before, period.GetProperty("version").GetInt64());
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task AnApprovedPayingQuincenaBlocksMovingTheEnd()
    {
        var scene = await NewSceneAsync();
        var vacation = Single(await CreateAsync(scene, QepBody("2026-12-14", "2026-12-24")));
        await LiquidatedAsync(scene);
        await ApprovedAsync(scene);

        // Two more days of vacation change what the approved quincena paid: vacation_pay.
        var response = await UpdateAsync(scene, vacation.GetProperty("id").GetGuid(), QepBody("2026-12-14", "2026-12-26"), Etag(vacation));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await JsonOfAsync(response);
        Assert.Equal("payroll.period.not_editable", CodeOf(problem));
        Assert.Equal("Reabre la quincena para cambiar estas vacaciones.", problem.GetProperty("detail").GetString());
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task AnApprovedQuincenaTurnsOffWhatItBlocks()
    {
        var scene = await NewSceneAsync();
        var vacation = Single(await CreateAsync(scene, QepBody("2026-12-14", "2026-12-24")));
        await LiquidatedAsync(scene);
        await ApprovedAsync(scene);

        var list = await OkAsync(await scene.Tenant.Payroll.GetAsync(VacationsUrl(scene), Ct));

        var can = Single(list).GetProperty("can");
        Assert.False(can.GetProperty("update").GetBoolean());
        Assert.False(can.GetProperty("delete").GetBoolean());
        Assert.Equal("Approved", Single(list).GetProperty("paidIn").GetProperty("status").GetString());
        var delete = await DeleteAsync(scene, vacation.GetProperty("id").GetGuid(), Etag(vacation));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, delete.StatusCode);
        Assert.Equal("payroll.period.not_editable", CodeOf(await JsonOfAsync(delete)));
        scene.Tenant.Dispose();
    }

    // External 2026-12-10..20 with the first quincena of December approved: the example of the spec («mover el fin dentro de una
    // quincena no aprobada pasa»). The first quincena reads the dates clipped to 12-10..15, so a later end changes nothing it calculated.
    private async Task<(Scene Scene, JsonElement Vacation)> ApprovedWithAnExternalAsync()
    {
        var scene = await NewSceneAsync();
        var vacation = Single(await CreateAsync(scene, ExternalBody("2026-12-10", "2026-12-20", businessDays: 7)));
        await LiquidatedAsync(scene);
        await ApprovedAsync(scene);
        return (scene, vacation);
    }

    [Fact]
    public async Task AChangeThatDoesNotReachTheApprovedOnePasses()
    {
        var (scene, vacation) = await ApprovedWithAnExternalAsync();
        var id = vacation.GetProperty("id").GetGuid();

        var moved = await OkAsync(await UpdateAsync(scene, id, ExternalBody("2026-12-10", "2026-12-22", businessDays: 9), Etag(vacation)));
        var earlier = await UpdateAsync(scene, id, ExternalBody("2026-12-11", "2026-12-22", businessDays: 8), Etag(Single(moved)));

        Assert.Equal("2026-12-22", Single(moved).GetProperty("endDate").GetString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, earlier.StatusCode);
        Assert.Equal("payroll.period.not_editable", CodeOf(await JsonOfAsync(earlier)));
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task AnApprovedOneThatHrMovedDoesNotBlockAHarmlessEdit()
    {
        var (scene, vacation) = await ApprovedWithAnExternalAsync();

        // Hr records a raise on the 5th: what the approved quincena would calculate today is no longer what it stored.
        var raise = await scene.Tenant.Payroll.PostAsJsonAsync(
            $"{scene.Tenant.HrBase}/employees/{scene.EmployeeId}/contracts/{scene.ContractId}/salaries",
            new { effectiveDate = "2026-12-05", amount = 3_200_000m },
            Ct);
        Assert.True(raise.StatusCode == HttpStatusCode.OK || raise.StatusCode == HttpStatusCode.Created, await BodyAsync(raise));

        var response = await UpdateAsync(
            scene, vacation.GetProperty("id").GetGuid(), ExternalBody("2026-12-10", "2026-12-22", businessDays: 9), Etag(vacation));

        Assert.True(response.StatusCode == HttpStatusCode.OK, await BodyAsync(response));
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task AnInactiveCompanyCannotRegister()
    {
        var scene = await NewSceneAsync();
        await DeactivateCompanyAsync(scene.Tenant.Admin, scene.Tenant.Id, scene.Setup.Company.Id);

        var response = await scene.Tenant.Payroll.PostAsJsonAsync(VacationsUrl(scene), QepBody("2026-12-14", "2026-12-24"), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("payroll.company.inactive", CodeOf(await JsonOfAsync(response)));
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task ThePeriodDetailListsTheVacations()
    {
        var scene = await NewSceneAsync();
        await CreateAsync(scene, QepBody("2026-12-14", "2026-12-24"));

        var detail = await OkAsync(await scene.Tenant.Payroll.GetAsync(scene.PeriodUrl, Ct));

        // Two of the eleven payroll days fall in this quincena (the 14th and the 15th); the dates are those of the vacation.
        var vacation = Single(detail.GetProperty("vacations"));
        Assert.Equal(scene.EmployeeId, vacation.GetProperty("employeeId").GetGuid());
        Assert.Equal("Ana Pérez", vacation.GetProperty("fullName").GetString());
        Assert.Equal(("2026-12-14", "2026-12-24", 2), (
            vacation.GetProperty("startDate").GetString(), vacation.GetProperty("endDate").GetString(), vacation.GetProperty("days").GetInt32()));
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task TheEmployeeDetailCarriesThemAndTheRowItsDays()
    {
        var scene = await NewSceneAsync();
        await CreateAsync(scene, QepBody("2026-12-14", "2026-12-24"));
        var before = await OkAsync(await scene.Tenant.Payroll.GetAsync($"{scene.PeriodUrl}/employees/{scene.EmployeeId}", Ct));
        Assert.Equal(JsonValueKind.Null, before.GetProperty("employee").GetProperty("vacationDays").ValueKind);
        await LiquidatedAsync(scene);

        var detail = await OkAsync(await scene.Tenant.Payroll.GetAsync($"{scene.PeriodUrl}/employees/{scene.EmployeeId}", Ct));

        var employee = detail.GetProperty("employee");
        Assert.Equal((2, 13), (employee.GetProperty("vacationDays").GetInt32(), employee.GetProperty("workedDays").GetInt32()));
        // 3 000 000 ÷ 30 × 11 payroll days of the whole vacation, paid in advance in this quincena.
        Assert.Equal(1_100_000m, detail.GetProperty("result").GetProperty("summary").GetProperty("vacationPay").GetDecimal());
        var vacation = Single(detail.GetProperty("vacations"));
        Assert.Equal("Liquidated", vacation.GetProperty("paidIn").GetProperty("status").GetString());
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task WritingNeedsReadAndManage()
    {
        var scene = await NewSceneAsync();
        using var reader = scene.Tenant.Client(PayrollPermissions.PeriodRead);
        using var manager = scene.Tenant.Client(PayrollPermissions.PeriodManage);

        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync(VacationsUrl(scene), QepBody("2026-12-14", "2026-12-24"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PostAsJsonAsync(VacationsUrl(scene), QepBody("2026-12-14", "2026-12-24"), Ct)).StatusCode);
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
