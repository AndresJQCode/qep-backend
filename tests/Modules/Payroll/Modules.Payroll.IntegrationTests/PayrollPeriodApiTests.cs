using System.Net;
using System.Text.Json;
using Modules.Payroll.Application;
using static Modules.Payroll.IntegrationTests.PayrollApiHarness;
using static Modules.Payroll.IntegrationTests.PayrollApiSeeds;

namespace Modules.Payroll.IntegrationTests;

/// <summary>
/// Abrir las quincenas y leerlas, sin cálculo todavía. El reloj del tenant arranca el 2026-11-10 y cada prueba lo mueve si lo
/// necesita. Los empleados se dan de alta por la API de Hr; los de las pruebas abren períodos con un cliente que puede todo.
/// </summary>
public sealed class PayrollPeriodApiTests : IClassFixture<PayrollApiFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly PayrollApiFixture _fixture;

    public PayrollPeriodApiTests(PayrollApiFixture fixture) => _fixture = fixture;

    private static string CompanyUrl(PayrollTenant tenant, Guid companyId) => $"{tenant.Url}/companies/{companyId}";

    private static Task<HttpResponseMessage> EnsureAsync(HttpClient client, PayrollTenant tenant, Guid companyId) =>
        client.PostAsync($"{CompanyUrl(tenant, companyId)}/periods/ensure-current", null, Ct);

    private static async Task<JsonElement> EnsuredAsync(PayrollTenant tenant, Guid companyId)
    {
        var response = await EnsureAsync(tenant.Payroll, tenant, companyId);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await BodyAsync(response));
        return await JsonOfAsync(response);
    }

    private static async Task<JsonElement> DetailAsync(HttpClient client, PayrollTenant tenant, Guid periodId)
    {
        var response = await client.GetAsync($"{tenant.Url}/periods/{periodId}", Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await BodyAsync(response));
        return await JsonOfAsync(response);
    }

    private static Guid FirstPeriodId(JsonElement list) => list.GetProperty("items")[0].GetProperty("id").GetGuid();

    private static List<string> StartDates(JsonElement list) =>
        list.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("startDate").GetString()!).ToList();

    [Fact]
    public async Task TheFirstPeriodOfACompanyIsTheCurrentFirstHalf()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);

        var list = await EnsuredAsync(tenant, setup.Company.Id);

        var period = Assert.Single(list.GetProperty("items").EnumerateArray());
        Assert.Equal("2026-11-01", period.GetProperty("startDate").GetString());
        Assert.Equal("2026-11-15", period.GetProperty("endDate").GetString());
        Assert.Equal("First", period.GetProperty("half").GetString());
        Assert.Equal("Draft", period.GetProperty("status").GetString());
        Assert.Equal(1, period.GetProperty("version").GetInt64());
        Assert.False(period.GetProperty("outdated").GetBoolean());
        Assert.False(period.GetProperty("sourceChanged").GetBoolean());
        Assert.Equal(setup.Company.Id, period.GetProperty("company").GetProperty("id").GetGuid());
        Assert.Equal(JsonValueKind.Null, period.GetProperty("totals").ValueKind);
        Assert.Equal(JsonValueKind.Null, list.GetProperty("firstPeriodStartsOn").ValueKind);
    }

    [Fact]
    public async Task InASecondHalfNothingOpensUntilNextMonth()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        _fixture.Clock.UtcNow = new DateTimeOffset(2026, 11, 20, 15, 0, 0, TimeSpan.Zero);

        var ensured = await EnsuredAsync(tenant, setup.Company.Id);
        var listed = await (await tenant.Payroll.GetAsync($"{CompanyUrl(tenant, setup.Company.Id)}/periods", Ct)).Content
            .ReadAsStringAsync(Ct);

        Assert.Equal(0, ensured.GetProperty("items").GetArrayLength());
        Assert.Equal("2026-12-01", ensured.GetProperty("firstPeriodStartsOn").GetString());
        Assert.Contains("\"firstPeriodStartsOn\":\"2026-12-01\"", listed);
    }

    // Review Focus 1: a las 23:30 de Bogotá del 15 el UTC ya es el 16, pero para el tenant todavía es la primera quincena.
    [Fact]
    public async Task TheCurrentPeriodUsesTheTenantDayNotUtc()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        await EnsuredAsync(tenant, setup.Company.Id);
        _fixture.Clock.UtcNow = new DateTimeOffset(2026, 11, 16, 4, 30, 0, TimeSpan.Zero);

        var list = await EnsuredAsync(tenant, setup.Company.Id);

        Assert.Equal(["2026-11-01"], StartDates(list));
    }

    [Fact]
    public async Task EnsuringFillsTheGapsWithoutHoles()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        await EnsuredAsync(tenant, setup.Company.Id);
        _fixture.Clock.UtcNow = new DateTimeOffset(2026, 12, 20, 15, 0, 0, TimeSpan.Zero);

        var list = await EnsuredAsync(tenant, setup.Company.Id);

        Assert.Equal(["2026-12-16", "2026-12-01", "2026-11-16", "2026-11-01"], StartDates(list));
        Assert.Equal(
            ["Second", "First", "Second", "First"],
            list.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("half").GetString()));
    }

    [Fact]
    public async Task EnsuringTwiceIsIdempotent()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);

        var first = await EnsuredAsync(tenant, setup.Company.Id);
        var second = await EnsuredAsync(tenant, setup.Company.Id);

        Assert.Equal(FirstPeriodId(first), FirstPeriodId(second));
        Assert.Equal(1, second.GetProperty("items").GetArrayLength());
        Assert.Equal(1, second.GetProperty("items")[0].GetProperty("version").GetInt64());
    }

    [Fact]
    public async Task AnInactiveCompanyDoesNotOpenPeriods()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        await DeactivateCompanyAsync(tenant.Admin, tenant.Id, setup.Company.Id);

        var response = await EnsureAsync(tenant.Payroll, tenant, setup.Company.Id);

        Assert.Equal((HttpStatusCode)422, response.StatusCode);
        Assert.Equal("payroll.company.inactive", CodeOf(await JsonOfAsync(response)));
    }

    private static Task<HttpResponseMessage> OpenNextAsync(HttpClient client, PayrollTenant tenant, Guid companyId) =>
        client.PostAsync($"{CompanyUrl(tenant, companyId)}/periods/open-next", null, Ct);

    private static async Task<JsonElement> OpenedNextAsync(PayrollTenant tenant, Guid companyId)
    {
        var response = await OpenNextAsync(tenant.Payroll, tenant, companyId);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await BodyAsync(response));
        return await JsonOfAsync(response);
    }

    [Fact]
    public async Task OpeningNextOpensTheFollowingHalfAndRespondsTheList()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        _fixture.Clock.UtcNow = new DateTimeOffset(2026, 11, 10, 15, 0, 0, TimeSpan.Zero);
        var ensured = await EnsuredAsync(tenant, setup.Company.Id);
        Assert.Equal("2026-11-16", ensured.GetProperty("nextPeriodStartsOn").GetString());

        var list = await OpenedNextAsync(tenant, setup.Company.Id);

        Assert.Equal(["2026-11-16", "2026-11-01"], StartDates(list));
        Assert.Equal(list.GetProperty("items")[1].GetProperty("id").GetGuid(), list.GetProperty("currentPeriodId").GetGuid());
        Assert.Equal("2026-12-01", list.GetProperty("nextPeriodStartsOn").GetString());
    }

    [Fact]
    public async Task OpeningNextWithoutPeriodsInASecondHalfOpensNextMonth()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        _fixture.Clock.UtcNow = new DateTimeOffset(2026, 11, 22, 15, 0, 0, TimeSpan.Zero);

        var list = await OpenedNextAsync(tenant, setup.Company.Id);

        Assert.Equal(["2026-12-01"], StartDates(list));
        Assert.Equal(JsonValueKind.Null, list.GetProperty("currentPeriodId").ValueKind);
    }

    [Fact]
    public async Task OpeningNextOpensBothHalvesOfNextMonthAndStopsThere()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        _fixture.Clock.UtcNow = new DateTimeOffset(2026, 11, 22, 15, 0, 0, TimeSpan.Zero);

        var first = await OpenedNextAsync(tenant, setup.Company.Id);
        Assert.Equal("2026-12-16", first.GetProperty("nextPeriodStartsOn").GetString());
        var second = await OpenedNextAsync(tenant, setup.Company.Id);
        var third = await OpenedNextAsync(tenant, setup.Company.Id);

        Assert.Equal(["2026-12-16", "2026-12-01"], StartDates(second));
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextPeriodStartsOn").ValueKind);
        Assert.Equal(StartDates(second), StartDates(third));
        Assert.Equal(FirstPeriodId(second), FirstPeriodId(third));
    }

    [Fact]
    public async Task OpeningNextOnAnInactiveCompanyIs422()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        await DeactivateCompanyAsync(tenant.Admin, tenant.Id, setup.Company.Id);

        var response = await OpenNextAsync(tenant.Payroll, tenant, setup.Company.Id);

        Assert.Equal((HttpStatusCode)422, response.StatusCode);
        Assert.Equal("payroll.company.inactive", CodeOf(await JsonOfAsync(response)));
    }

    [Fact]
    public async Task OpeningNextWithoutPeriodManageIs403()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        using var readOnly = tenant.Client(PayrollPermissions.PeriodRead);

        var response = await OpenNextAsync(readOnly, tenant, setup.Company.Id);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task TheListCarriesNextPeriodStartsOn()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        _fixture.Clock.UtcNow = new DateTimeOffset(2026, 11, 10, 15, 0, 0, TimeSpan.Zero);
        await EnsuredAsync(tenant, setup.Company.Id);

        var listed = await tenant.Payroll.GetAsync($"{CompanyUrl(tenant, setup.Company.Id)}/periods", Ct);

        var body = await listed.Content.ReadAsStringAsync(Ct);
        Assert.Contains("\"nextPeriodStartsOn\":\"2026-11-16\"", body);
        Assert.Contains("\"currentPeriodId\":\"", body);
    }

    [Fact]
    public async Task AnUnknownCompanyIs404()
    {
        using var tenant = await NewTenantAsync(_fixture);

        var response = await EnsureAsync(tenant.Payroll, tenant, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("payroll.company.not_found", CodeOf(await JsonOfAsync(response)));
    }

    [Fact]
    public async Task TheDetailListsLiquidableExcludedAndBlocked()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var standard = await HireAsync(tenant, HireBody(setup, documentNumber: "1001", firstName: "Ana", lastName: "Pérez"));
        await HireAsync(tenant, HireBody(
            setup, salaryType: "Integral", amount: 25_000_000m, documentNumber: "1002", firstName: "Beto", lastName: "Ramírez"));
        var list = await EnsuredAsync(tenant, setup.Company.Id);

        var detail = await DetailAsync(tenant.Payroll, tenant, FirstPeriodId(list));

        var employee = Assert.Single(detail.GetProperty("employees").EnumerateArray());
        Assert.Equal(EmployeeIdOf(standard), employee.GetProperty("employeeId").GetGuid());
        Assert.Equal("Ana Pérez", employee.GetProperty("fullName").GetString());
        Assert.Equal("CC", employee.GetProperty("documentType").GetString());
        Assert.Equal("1001", employee.GetProperty("documentNumber").GetString());
        Assert.Equal("Analista", employee.GetProperty("position").GetString());
        Assert.Equal("Bodega - Principal", employee.GetProperty("workplace").GetString());
        Assert.Equal(JsonValueKind.Null, employee.GetProperty("workedDays").ValueKind);
        Assert.False(employee.GetProperty("hasInputs").GetBoolean());
        Assert.False(employee.GetProperty("finalSettlementPending").GetBoolean());
        Assert.Equal(JsonValueKind.Null, employee.GetProperty("summary").ValueKind);
        var excluded = Assert.Single(detail.GetProperty("excluded").EnumerateArray());
        Assert.Equal("SalaryTypeNotSupported", excluded.GetProperty("reason").GetString());
        Assert.Equal("Beto Ramírez", excluded.GetProperty("fullName").GetString());
        Assert.Equal(0, detail.GetProperty("blocked").GetArrayLength());
        var period = detail.GetProperty("period");
        Assert.Equal(JsonValueKind.Null, period.GetProperty("totals").ValueKind);
        Assert.Equal(1, period.GetProperty("employeeCount").GetInt32());
        Assert.True(period.GetProperty("can").GetProperty("liquidate").GetBoolean());
        Assert.Equal("Created", detail.GetProperty("history")[0].GetProperty("action").GetString());
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("history")[0].GetProperty("fromStatus").ValueKind);
        Assert.Equal("Draft", detail.GetProperty("history")[0].GetProperty("toStatus").GetString());
    }

    [Fact]
    public async Task CanFollowsTheCallerPermissions()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var periodId = FirstPeriodId(await EnsuredAsync(tenant, setup.Company.Id));
        using var readOnly = tenant.Client(PayrollPermissions.PeriodRead);
        using var manager = tenant.Client(PayrollPermissions.PeriodRead, PayrollPermissions.PeriodManage);

        var readCan = (await DetailAsync(readOnly, tenant, periodId)).GetProperty("period").GetProperty("can");
        var manageCan = (await DetailAsync(manager, tenant, periodId)).GetProperty("period").GetProperty("can");

        // En Borrador tampoco se exporta, así que quien sólo lee ve los seis en false.
        foreach (var name in new[] { "capture", "liquidate", "approve", "close", "reopen", "export" })
        {
            Assert.False(readCan.GetProperty(name).GetBoolean(), name);
        }

        Assert.True(manageCan.GetProperty("capture").GetBoolean());
        Assert.True(manageCan.GetProperty("liquidate").GetBoolean());
        foreach (var name in new[] { "approve", "close", "reopen", "export" })
        {
            Assert.False(manageCan.GetProperty(name).GetBoolean(), name);
        }

        // Un Borrador no se aprueba aunque quien llame pueda: `status` explica por qué.
        var blockedBy = (await DetailAsync(tenant.Payroll, tenant, periodId)).GetProperty("period").GetProperty("approveBlockedBy");
        Assert.Equal("status", Assert.Single(blockedBy.EnumerateArray()).GetString());
    }

    [Fact]
    public async Task AnUnknownPeriodIs404()
    {
        using var tenant = await NewTenantAsync(_fixture);

        var response = await tenant.Payroll.GetAsync($"{tenant.Url}/periods/{Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("payroll.period.not_found", CodeOf(await JsonOfAsync(response)));
    }

    // Review Focus 6: el id de un período de otro tenant pedido con la ruta del propio responde lo mismo que un id inventado.
    [Fact]
    public async Task AnotherTenantsPeriodIs404AndItsRouteIs403()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var periodId = FirstPeriodId(await EnsuredAsync(tenant, setup.Company.Id));
        using var other = await NewTenantAsync(_fixture);

        var withOwnRoute = await other.Payroll.GetAsync($"{other.Url}/periods/{periodId}", Ct);
        var withTheirRoute = await other.Payroll.GetAsync($"{tenant.Url}/periods/{periodId}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, withOwnRoute.StatusCode);
        Assert.Equal("payroll.period.not_found", CodeOf(await JsonOfAsync(withOwnRoute)));
        Assert.Equal(HttpStatusCode.Forbidden, withTheirRoute.StatusCode);
        Assert.Equal("authorization.denied", CodeOf(await JsonOfAsync(withTheirRoute)));
    }

    [Fact]
    public async Task EnsuringIsAudited()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);

        var periodId = FirstPeriodId(await EnsuredAsync(tenant, setup.Company.Id));

        Assert.Equal(1, await CountAuditEntriesAsync(_fixture.Factory, PayrollAuditActions.PeriodCreated, periodId.ToString()));
    }

    // Decisión del owner 2026-11-21: quien sólo configura no ve períodos.
    [Fact]
    public async Task ASettingsOnlyRoleCannotReadPeriods()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var periodId = FirstPeriodId(await EnsuredAsync(tenant, setup.Company.Id));
        using var settingsOnly = tenant.Client(PayrollPermissions.SettingsManage);

        var ensure = await EnsureAsync(settingsOnly, tenant, setup.Company.Id);
        var list = await settingsOnly.GetAsync($"{CompanyUrl(tenant, setup.Company.Id)}/periods", Ct);
        var detail = await settingsOnly.GetAsync($"{tenant.Url}/periods/{periodId}", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, ensure.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, detail.StatusCode);
    }

    // I-6: abrir es de `manage`; quien sólo lee usa GET, que no crea nada.
    [Fact]
    public async Task EnsuringNeedsPeriodManage()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        using var readOnly = tenant.Client(PayrollPermissions.PeriodRead);

        var ensure = await EnsureAsync(readOnly, tenant, setup.Company.Id);
        var list = await readOnly.GetAsync($"{CompanyUrl(tenant, setup.Company.Id)}/periods", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, ensure.StatusCode);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal(0, (await JsonOfAsync(list)).GetProperty("items").GetArrayLength());
    }

    // I-9: un bloqueado también es una fila; `blocked` sólo repite su id y sus motivos.
    [Fact]
    public async Task ABlockedEmployeeIsAlsoAnEmployeeRow()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var hired = await HireAsync(tenant, setup);
        var employeeId = EmployeeIdOf(hired);
        var periodId = FirstPeriodId(await EnsuredAsync(tenant, setup.Company.Id));
        _fixture.StripAssignmentsOf = employeeId;

        var detail = await DetailAsync(tenant.Payroll, tenant, periodId);

        var row = Assert.Single(detail.GetProperty("employees").EnumerateArray());
        Assert.Equal(employeeId, row.GetProperty("employeeId").GetGuid());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("position").ValueKind);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("workplace").ValueKind);
        var blocked = Assert.Single(detail.GetProperty("blocked").EnumerateArray());
        Assert.Equal(employeeId, blocked.GetProperty("employeeId").GetGuid());
        Assert.Equal(["MissingWorkplace"], blocked.GetProperty("reasons").EnumerateArray().Select(reason => reason.GetString()));
        Assert.Equal(2, blocked.EnumerateObject().Count());
        Assert.Equal(1, detail.GetProperty("period").GetProperty("employeeCount").GetInt32());
    }
}
