using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Payroll.Application;
using Modules.Payroll.Domain.Engine;
using static Modules.Payroll.IntegrationTests.PayrollApiHarness;
using static Modules.Payroll.IntegrationTests.PayrollApiSeeds;

namespace Modules.Payroll.IntegrationTests;

/// <summary>
/// Liquidar, aprobar, cerrar y reabrir. Una empresa exonerada con un centro de clase I y un empleado con el salario mínimo
/// (1 750 905 desde 2025-01-01): es el ancla <c>minimum-wage-first-half</c> de los casos de oro. El reloj arranca el 2026-11-10 y
/// cada prueba lo mueve al 2026-11-25 cuando necesita la segunda quincena.
/// </summary>
public sealed class PayrollLiquidationApiTests : IClassFixture<PayrollApiFixture>
{
    private static readonly Guid Commissions = Guid.Parse("00000000-0000-0000-0002-000000000001");
    private static readonly Guid OtherIncome = Guid.Parse("00000000-0000-0000-0002-000000000004");
    private static readonly Guid OtherDeduction = Guid.Parse("00000000-0000-0000-0003-000000000001");

    private static readonly DateTimeOffset SecondHalfNow = new(2026, 11, 25, 15, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly PayrollApiFixture _fixture;

    public PayrollLiquidationApiTests(PayrollApiFixture fixture) => _fixture = fixture;

    private sealed record Scene(PayrollTenant Tenant, SeededSetup Setup, JsonElement Employee, Guid PeriodId)
    {
        public Guid EmployeeId => EmployeeIdOf(Employee);

        public string PeriodUrl(Guid? periodId = null) => $"{Tenant.Url}/periods/{periodId ?? PeriodId}";

        public string CompanyUrl => $"{Tenant.Url}/companies/{Setup.Company.Id}";
    }

    // Una empresa con centro de clase I (el ancla de oro) y su empleado con el salario mínimo.
    private async Task<Scene> NewSceneAsync(decimal amount = 1_750_905m, bool openFirstHalf = true)
    {
        var tenant = await NewTenantAsync(_fixture);
        var company = await SeedCompanyAsync(tenant, "Comercializadora QEP S.A.S.", "Principal", "Bodega", riskClass: "I");
        var area = await CreatedAsync(tenant.Payroll, $"{tenant.HrBase}/areas", new { name = "Operaciones" });
        var position = await CreatedAsync(tenant.Payroll, $"{tenant.HrBase}/positions", new { name = "Analista" });
        var setup = new SeededSetup(
            company.Company, company.SiteId, company.WorkplaceId, area.GetProperty("id").GetGuid(), position.GetProperty("id").GetGuid());
        var employee = await HireAsync(tenant, HireBody(setup, startDate: "2025-01-01", amount: amount));

        var periodId = Guid.Empty;
        if (openFirstHalf)
        {
            periodId = (await EnsureAsync(tenant, setup)).GetProperty("items")[0].GetProperty("id").GetGuid();
        }

        return new Scene(tenant, setup, employee, periodId);
    }

    private static async Task<JsonElement> EnsureAsync(PayrollTenant tenant, SeededSetup setup) =>
        await EnsureAsync(tenant, setup.Company.Id);

    private static async Task<JsonElement> EnsureAsync(PayrollTenant tenant, Guid companyId)
    {
        var response = await tenant.Payroll.PostAsync($"{tenant.Url}/companies/{companyId}/periods/ensure-current", null, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await BodyAsync(response));
        return await JsonOfAsync(response);
    }

    /// <summary>Abre la segunda quincena (el reloj pasa al 25) y devuelve su id.</summary>
    private async Task<Guid> OpenSecondHalfAsync(Scene scene)
    {
        _fixture.Clock.UtcNow = SecondHalfNow;
        var list = await EnsureAsync(scene.Tenant, scene.Setup);
        return list.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("half").GetString() == "Second").GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> DetailAsync(HttpClient client, Scene scene, Guid? periodId = null)
    {
        var response = await client.GetAsync(scene.PeriodUrl(periodId), Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await BodyAsync(response));
        return await JsonOfAsync(response);
    }

    private static async Task<string> VersionAsync(HttpClient client, Scene scene, Guid? periodId = null) =>
        $"\"{(await DetailAsync(client, scene, periodId)).GetProperty("period").GetProperty("version").GetInt64()}\"";

    private static Task<HttpResponseMessage> PostWithVersionAsync(
        HttpClient client, Scene scene, string action, Guid? periodId = null, object? body = null, string? ifMatch = null) =>
        PostWithVersionCoreAsync(client, scene, action, periodId, body, ifMatch);

    private static async Task<HttpResponseMessage> PostWithVersionCoreAsync(
        HttpClient client, Scene scene, string action, Guid? periodId, object? body, string? ifMatch) =>
        await PostAsync(client, $"{scene.PeriodUrl(periodId)}/{action}", ifMatch ?? await VersionAsync(client, scene, periodId), body);

    private static readonly object Confirmed = new { confirmNoAbsences = true };

    private static async Task<JsonElement> OkAsync(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode == HttpStatusCode.OK, await BodyAsync(response));
        return await JsonOfAsync(response);
    }

    private static async Task<JsonElement> LiquidatedAsync(Scene scene, Guid? periodId = null, HttpClient? client = null) =>
        await OkAsync(await PostWithVersionAsync(client ?? scene.Tenant.Payroll, scene, "liquidate", periodId, Confirmed));

    private static async Task<JsonElement> ApprovedAsync(Scene scene, Guid? periodId = null, HttpClient? client = null) =>
        await OkAsync(await PostWithVersionAsync(client ?? scene.Tenant.Payroll, scene, "approve", periodId));

    private static async Task<HttpResponseMessage> PostSimpleAsync(HttpClient client, string url) => await client.PostAsync(url, null, Ct);

    private static async Task<JsonElement> ProblemOfAsync(HttpResponseMessage response, string code)
    {
        Assert.Equal((HttpStatusCode)422, response.StatusCode);
        var problem = await JsonOfAsync(response);
        Assert.Equal(code, CodeOf(problem));
        return problem;
    }

    private static JsonElement LineOf(JsonElement employeeDetail, string code) =>
        employeeDetail.GetProperty("result").GetProperty("lines").EnumerateArray()
            .Single(line => line.GetProperty("code").GetString() == code);

    private static Task<HttpResponseMessage> EmployeeAsync(Scene scene, Guid? periodId = null) =>
        scene.Tenant.Payroll.GetAsync($"{scene.PeriodUrl(periodId)}/employees/{scene.EmployeeId}", Ct);

    [Fact]
    public async Task LiquidatingTheFirstHalfMatchesTheAnchor()
    {
        var scene = await NewSceneAsync();

        var detail = await LiquidatedAsync(scene);

        var period = detail.GetProperty("period");
        Assert.Equal("Liquidated", period.GetProperty("status").GetString());
        Assert.Equal(929_965m, period.GetProperty("totals").GetProperty("net").GetDecimal());
        Assert.Equal(1_357_907m, period.GetProperty("totals").GetProperty("employerCost").GetDecimal());
        Assert.Equal("2026-11-01", period.GetProperty("legalValuesDate").GetString());
        Assert.Equal(PayrollEngine.Version, period.GetProperty("engineVersion").GetString());
        Assert.False(period.GetProperty("outdated").GetBoolean());
        Assert.False(period.GetProperty("sourceChanged").GetBoolean());
        Assert.Equal(1, period.GetProperty("employeeCount").GetInt32());
        Assert.Equal(15, Assert.Single(detail.GetProperty("employees").EnumerateArray()).GetProperty("workedDays").GetInt32());

        var employee = await OkAsync(await EmployeeAsync(scene));
        var result = employee.GetProperty("result");
        Assert.Equal(PayrollEngine.Version, result.GetProperty("engineVersion").GetString());
        Assert.Equal(875_453m, LineOf(employee, "salary").GetProperty("amount").GetDecimal());
        Assert.Equal(124_548m, LineOf(employee, "transport_allowance").GetProperty("amount").GetDecimal());
        Assert.Equal(35_018m, LineOf(employee, "health_employee").GetProperty("amount").GetDecimal());
        Assert.Equal(4_600m, LineOf(employee, "arl").GetProperty("amount").GetDecimal());
        Assert.Equal(36_506m, LineOf(employee, "vacation").GetProperty("amount").GetDecimal());
        Assert.Equal(929_965m, result.GetProperty("summary").GetProperty("net").GetDecimal());

        var health = LineOf(employee, "health_employee").GetProperty("trace").GetProperty("legalValues")
            .EnumerateArray().Single(value => value.GetProperty("key").GetString() == "HealthEmployeeRate");
        Assert.Equal("Ley 100/1993 art. 204", health.GetProperty("basis").GetString());
        Assert.Equal(0.04m, health.GetProperty("value").GetDecimal());
        Assert.Contains(
            result.GetProperty("legalValues").EnumerateArray(),
            value => value.GetProperty("key").GetString() == "Smmlv");
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task TheSecondHalfSettlesTheMonth()
    {
        var scene = await NewSceneAsync();
        await LiquidatedAsync(scene);
        var second = await OpenSecondHalfAsync(scene);

        var detail = await LiquidatedAsync(scene, second);

        var totals = detail.GetProperty("period").GetProperty("totals");
        Assert.Equal(929_963m, totals.GetProperty("net").GetDecimal());
        Assert.Equal(1_357_805m, totals.GetProperty("employerCost").GetDecimal());
        var employee = await OkAsync(await EmployeeAsync(scene, second));
        Assert.Equal(875_452m, LineOf(employee, "salary").GetProperty("amount").GetDecimal());
        Assert.Equal(35_018m, LineOf(employee, "health_employee").GetProperty("amount").GetDecimal());
        scene.Tenant.Dispose();
    }

    // Review Focus 7: lo que el motor no sabe calcular no se liquida y se dice por qué.
    [Fact]
    public async Task UnsupportedEmployeesAreExcludedWithTheirReason()
    {
        var scene = await NewSceneAsync();
        var integral = EmployeeIdOf(await HireAsync(
            scene.Tenant,
            HireBody(scene.Setup, salaryType: "Integral", amount: 25_000_000m, documentNumber: "1002", firstName: "Beto", lastName: "Ramírez")));

        var detail = await LiquidatedAsync(scene);

        Assert.Equal(1, detail.GetProperty("employees").GetArrayLength());
        var excluded = Assert.Single(detail.GetProperty("excluded").EnumerateArray());
        Assert.Equal(integral, excluded.GetProperty("employeeId").GetGuid());
        Assert.Equal("SalaryTypeNotSupported", excluded.GetProperty("reason").GetString());
        var excludedDetail = await OkAsync(await scene.Tenant.Payroll.GetAsync($"{scene.PeriodUrl()}/employees/{integral}", Ct));
        Assert.Equal(JsonValueKind.Null, excludedDetail.GetProperty("result").ValueKind);
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task LiquidatingNeedsTheAbsenceConfirmation()
    {
        var scene = await NewSceneAsync();

        var denied = await PostWithVersionAsync(scene.Tenant.Payroll, scene, "liquidate", body: new { confirmNoAbsences = false });
        var missing = await PostWithVersionAsync(scene.Tenant.Payroll, scene, "liquidate");

        await ProblemOfAsync(denied, "payroll.liquidation.absences_unconfirmed");
        await ProblemOfAsync(missing, "payroll.liquidation.absences_unconfirmed");
        Assert.Equal("Draft", (await DetailAsync(scene.Tenant.Payroll, scene)).GetProperty("period").GetProperty("status").GetString());
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task ABlockedEmployeeStopsTheLiquidation()
    {
        var scene = await NewSceneAsync();
        _fixture.StripAssignmentsOf = scene.EmployeeId;

        var response = await PostWithVersionAsync(scene.Tenant.Payroll, scene, "liquidate", body: Confirmed);

        await ProblemOfAsync(response, "payroll.liquidation.blocked");
        var detail = await DetailAsync(scene.Tenant.Payroll, scene);
        var blocked = Assert.Single(detail.GetProperty("blocked").EnumerateArray());
        Assert.Equal(["MissingWorkplace"], blocked.GetProperty("reasons").EnumerateArray().Select(reason => reason.GetString()));
        Assert.Equal("Draft", detail.GetProperty("period").GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("period").GetProperty("totals").ValueKind);
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task NobodyToLiquidateIs422()
    {
        var scene = await NewSceneForExcludedOnlyAsync();

        var response = await PostWithVersionAsync(scene.Tenant.Payroll, scene, "liquidate", body: Confirmed);

        await ProblemOfAsync(response, "payroll.liquidation.nobody_to_liquidate");
        scene.Tenant.Dispose();
    }

    // Un tenant cuyo único empleado es integral.
    private async Task<Scene> NewSceneForExcludedOnlyAsync()
    {
        var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var employee = await HireAsync(
            tenant, HireBody(setup, salaryType: "Integral", amount: 25_000_000m, documentNumber: "1002", startDate: "2025-01-01"));
        var periodId = (await EnsureAsync(tenant, setup.Company.Id)).GetProperty("items")[0].GetProperty("id").GetGuid();
        return new Scene(tenant, setup, employee, periodId);
    }

    [Fact]
    public async Task TheSecondHalfNeedsTheFirstLiquidated()
    {
        var scene = await NewSceneAsync();
        var second = await OpenSecondHalfAsync(scene);

        var response = await PostWithVersionAsync(scene.Tenant.Payroll, scene, "liquidate", second, Confirmed);

        await ProblemOfAsync(response, "payroll.period.first_half_not_liquidated");
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task ReliquidatingTheFirstHalfOutdatesTheSecond()
    {
        var scene = await NewSceneAsync();
        await LiquidatedAsync(scene);
        var second = await OpenSecondHalfAsync(scene);
        await LiquidatedAsync(scene, second);

        await LiquidatedAsync(scene);

        var detail = await DetailAsync(scene.Tenant.Payroll, scene, second);
        Assert.True(detail.GetProperty("period").GetProperty("outdated").GetBoolean());
        Assert.Contains("outdated", detail.GetProperty("period").GetProperty("approveBlockedBy").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal("MarkedOutdated", detail.GetProperty("history")[0].GetProperty("action").GetString());
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task ReopeningTheFirstHalfOutdatesTheSecond()
    {
        var scene = await NewSceneAsync();
        await LiquidatedAsync(scene);
        await ApprovedAsync(scene);
        var second = await OpenSecondHalfAsync(scene);
        await LiquidatedAsync(scene, second);

        await OkAsync(await PostWithVersionAsync(scene.Tenant.Payroll, scene, "reopen", body: new { reason = "Faltó una comisión" }));

        var detail = await DetailAsync(scene.Tenant.Payroll, scene, second);
        Assert.True(detail.GetProperty("period").GetProperty("outdated").GetBoolean());
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task ApprovingTheSecondNeedsTheFirstApproved()
    {
        var scene = await NewSceneAsync();
        await LiquidatedAsync(scene);
        var second = await OpenSecondHalfAsync(scene);
        var liquidated = await LiquidatedAsync(scene, second);
        Assert.Equal(
            ["first_half_not_approved"],
            liquidated.GetProperty("period").GetProperty("approveBlockedBy").EnumerateArray().Select(item => item.GetString()));

        var response = await PostWithVersionAsync(scene.Tenant.Payroll, scene, "approve", second);

        await ProblemOfAsync(response, "payroll.period.first_half_not_approved");
        scene.Tenant.Dispose();
    }

    private static async Task EnableDualControlAsync(Scene scene)
    {
        var response = await PutAsync(
            scene.Tenant.Payroll,
            $"{scene.CompanyUrl}/settings",
            new { transportFrequency = "Biweekly", transportCapBase = "SalaryOnly", overtimeBase = "SalaryOnly", exonerated = true, dualControl = true },
            "\"0\"");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // Review Focus 4: con doble control quien liquidó por última vez no aprueba, aunque haya reabierto otro.
    [Fact]
    public async Task WithDualControlTheLastLiquidatorCannotApprove()
    {
        var scene = await NewSceneAsync();
        await EnableDualControlAsync(scene);
        using var beto = CreateClient(_fixture.Factory, OtherSubjectId, scene.Tenant.IdText, AllPermissions);

        var liquidated = await LiquidatedAsync(scene);
        Assert.Contains(
            "dual_control",
            liquidated.GetProperty("period").GetProperty("approveBlockedBy").EnumerateArray().Select(item => item.GetString()));
        await ProblemOfAsync(await PostWithVersionAsync(scene.Tenant.Payroll, scene, "approve"), "payroll.period.same_person_cannot_approve");

        await ApprovedAsync(scene, client: beto);

        await OkAsync(await PostWithVersionAsync(scene.Tenant.Payroll, scene, "reopen", body: new { reason = "Corregir" }));
        await LiquidatedAsync(scene, client: beto);
        await ProblemOfAsync(await PostWithVersionAsync(beto, scene, "approve"), "payroll.period.same_person_cannot_approve");
        var approved = await ApprovedAsync(scene);
        Assert.Equal("Approved", approved.GetProperty("period").GetProperty("status").GetString());
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task WithoutDualControlTheLiquidatorApproves()
    {
        var scene = await NewSceneAsync();

        await LiquidatedAsync(scene);
        var approved = await ApprovedAsync(scene);

        Assert.Equal("Approved", approved.GetProperty("period").GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, approved.GetProperty("period").GetProperty("approvedBy").ValueKind);
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task ANegativeNetBlocksApproval()
    {
        var scene = await NewSceneAsync();
        var version = await VersionAsync(scene.Tenant.Payroll, scene);
        var captured = await PutAsync(
            scene.Tenant.Payroll,
            $"{scene.PeriodUrl()}/employees/{scene.EmployeeId}/inputs",
            new
            {
                overtime = Array.Empty<object>(),
                incomes = Array.Empty<object>(),
                deductions = new[] { new { conceptId = OtherDeduction, amount = 5_000_000m } },
            },
            version);
        Assert.Equal(HttpStatusCode.OK, captured.StatusCode);

        var liquidated = await LiquidatedAsync(scene);

        Assert.True(Assert.Single(liquidated.GetProperty("employees").EnumerateArray()).GetProperty("negativeNet").GetBoolean());
        Assert.Contains(
            "negative_net",
            liquidated.GetProperty("period").GetProperty("approveBlockedBy").EnumerateArray().Select(item => item.GetString()));
        await ProblemOfAsync(await PostWithVersionAsync(scene.Tenant.Payroll, scene, "approve"), "payroll.period.negative_net");
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task AChangeInHrAfterLiquidatingMarksSourceChanged()
    {
        var scene = await NewSceneAsync();
        await LiquidatedAsync(scene);
        var contractId = scene.Employee.GetProperty("contract").GetProperty("id").GetGuid();

        var raise = await scene.Tenant.Payroll.PostAsJsonAsync(
            $"{scene.Tenant.HrBase}/employees/{scene.EmployeeId}/contracts/{contractId}/salaries",
            new { effectiveDate = "2026-11-05", amount = 2_000_000m },
            Ct);
        Assert.True(raise.StatusCode == HttpStatusCode.OK, await BodyAsync(raise));

        var detail = await DetailAsync(scene.Tenant.Payroll, scene);
        Assert.True(detail.GetProperty("period").GetProperty("sourceChanged").GetBoolean());
        Assert.Contains(
            "source_changed",
            detail.GetProperty("period").GetProperty("approveBlockedBy").EnumerateArray().Select(item => item.GetString()));
        await ProblemOfAsync(await PostWithVersionAsync(scene.Tenant.Payroll, scene, "approve"), "payroll.period.outdated");

        var again = await LiquidatedAsync(scene);
        Assert.False(again.GetProperty("period").GetProperty("sourceChanged").GetBoolean());
        Assert.Empty(again.GetProperty("period").GetProperty("approveBlockedBy").EnumerateArray());
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task CapturingAfterLiquidatingOutdatesAndBlocksApproval()
    {
        var scene = await NewSceneAsync();
        await LiquidatedAsync(scene);
        var captured = await PutAsync(
            scene.Tenant.Payroll,
            $"{scene.PeriodUrl()}/employees/{scene.EmployeeId}/inputs",
            new
            {
                overtime = Array.Empty<object>(),
                incomes = new[] { new { conceptId = Commissions, amount = 100_000m, salaryConstitutive = true } },
                deductions = Array.Empty<object>(),
            },
            await VersionAsync(scene.Tenant.Payroll, scene));
        Assert.Equal(HttpStatusCode.OK, captured.StatusCode);

        var detail = await DetailAsync(scene.Tenant.Payroll, scene);
        Assert.True(detail.GetProperty("period").GetProperty("outdated").GetBoolean());
        await ProblemOfAsync(await PostWithVersionAsync(scene.Tenant.Payroll, scene, "approve"), "payroll.period.outdated");

        var again = await LiquidatedAsync(scene);
        Assert.False(again.GetProperty("period").GetProperty("outdated").GetBoolean());
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task CloseAndReopenKeepTheHistory()
    {
        var scene = await NewSceneAsync();
        await LiquidatedAsync(scene);
        await ApprovedAsync(scene);
        await OkAsync(await PostWithVersionAsync(scene.Tenant.Payroll, scene, "close"));

        var reopened = await OkAsync(await PostWithVersionAsync(scene.Tenant.Payroll, scene, "reopen", body: new { reason = "Faltó una comisión" }));

        var history = reopened.GetProperty("history").EnumerateArray().ToList();
        Assert.Equal(
            ["Reopened", "Closed", "Approved", "Liquidated", "Created"],
            history.Select(item => item.GetProperty("action").GetString()));
        Assert.Equal("Faltó una comisión", history[0].GetProperty("reason").GetString());
        Assert.Equal("Closed", history[0].GetProperty("fromStatus").GetString());
        Assert.True(history[3].GetProperty("absencesConfirmed").GetBoolean());
        Assert.Equal("Reopened", reopened.GetProperty("period").GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, reopened.GetProperty("period").GetProperty("approvedAt").ValueKind);
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task ReopenNeedsAReason()
    {
        var scene = await NewSceneAsync();
        await LiquidatedAsync(scene);
        await ApprovedAsync(scene);

        var blank = await PostWithVersionAsync(scene.Tenant.Payroll, scene, "reopen", body: new { reason = "  " });
        var missing = await PostWithVersionAsync(scene.Tenant.Payroll, scene, "reopen");
        var tooLong = await PostWithVersionAsync(scene.Tenant.Payroll, scene, "reopen", body: new { reason = new string('x', 501) });

        foreach (var response in new[] { blank, missing, tooLong })
        {
            Assert.Equal((HttpStatusCode)422, response.StatusCode);
            Assert.True(HasError(await JsonOfAsync(response), "reason"));
        }

        scene.Tenant.Dispose();
    }

    // Review Focus 5: una liquidación con If-Match viejo después de una captura es 412 y no deja resultados.
    [Fact]
    public async Task ALiquidationWithAStaleIfMatchIs412()
    {
        var scene = await NewSceneAsync();
        var stale = await VersionAsync(scene.Tenant.Payroll, scene);
        var captured = await PutAsync(
            scene.Tenant.Payroll,
            $"{scene.PeriodUrl()}/employees/{scene.EmployeeId}/inputs",
            new { overtime = Array.Empty<object>(), incomes = Array.Empty<object>(), deductions = Array.Empty<object>() },
            stale);
        Assert.Equal(HttpStatusCode.OK, captured.StatusCode);

        var response = await PostWithVersionAsync(scene.Tenant.Payroll, scene, "liquidate", body: Confirmed, ifMatch: stale);

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
        var detail = await DetailAsync(scene.Tenant.Payroll, scene);
        Assert.Equal("Draft", detail.GetProperty("period").GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("period").GetProperty("totals").ValueKind);
        var employee = await OkAsync(await EmployeeAsync(scene));
        Assert.Equal(JsonValueKind.Null, employee.GetProperty("result").ValueKind);

        var missing = await PostAsync(scene.Tenant.Payroll, $"{scene.PeriodUrl()}/liquidate", null, Confirmed);
        Assert.Equal((HttpStatusCode)428, missing.StatusCode);
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task RecurringCapAccumulatesAcrossPeriods()
    {
        var scene = await NewSceneAsync();
        var recurringUrl = $"{scene.CompanyUrl}/employees/{scene.EmployeeId}/recurring-concepts";
        var created = await scene.Tenant.Payroll.PostAsJsonAsync(
            recurringUrl,
            new { conceptId = OtherIncome, mode = "Fixed", amount = 100_000m, salaryConstitutive = false, cap = 150_000m },
            Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var recurringId = (await JsonOfAsync(created))[0].GetProperty("id").GetGuid();

        await LiquidatedAsync(scene);
        var firstEmployee = await OkAsync(await EmployeeAsync(scene));
        Assert.Equal(100_000m, LineOf(firstEmployee, $"recurring:{recurringId}").GetProperty("amount").GetDecimal());

        var second = await OpenSecondHalfAsync(scene);
        await LiquidatedAsync(scene, second);
        var secondEmployee = await OkAsync(await EmployeeAsync(scene, second));
        Assert.Equal(50_000m, LineOf(secondEmployee, $"recurring:{recurringId}").GetProperty("amount").GetDecimal());

        var list = await JsonOfAsync(await scene.Tenant.Payroll.GetAsync(recurringUrl, Ct));
        Assert.Equal("Exhausted", list[0].GetProperty("status").GetString());
        Assert.Equal(150_000m, list[0].GetProperty("accumulated").GetDecimal());
        Assert.Equal(0m, list[0].GetProperty("remaining").GetDecimal());
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task ApprovedPeriodsAreReadFromTheSnapshot()
    {
        var scene = await NewSceneAsync();
        await LiquidatedAsync(scene);
        var approved = await ApprovedAsync(scene);
        var contractId = scene.Employee.GetProperty("contract").GetProperty("id").GetGuid();

        var terminate = await scene.Tenant.Payroll.PostAsJsonAsync(
            $"{scene.Tenant.HrBase}/employees/{scene.EmployeeId}/contracts/{contractId}/terminate",
            new { reason = "Resignation", date = "2026-11-10", notes = (string?)null },
            Ct);
        Assert.True(terminate.StatusCode == HttpStatusCode.OK, await BodyAsync(terminate));

        var after = await DetailAsync(scene.Tenant.Payroll, scene);

        Assert.Equal(
            approved.GetProperty("employees").GetRawText(), after.GetProperty("employees").GetRawText());
        Assert.Equal(
            approved.GetProperty("period").GetProperty("totals").GetRawText(), after.GetProperty("period").GetProperty("totals").GetRawText());
        Assert.False(after.GetProperty("period").GetProperty("sourceChanged").GetBoolean());
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task LiquidationIsAudited()
    {
        var scene = await NewSceneAsync();

        await LiquidatedAsync(scene);

        Assert.Equal(1, await CountAuditEntriesAsync(_fixture.Factory, PayrollAuditActions.PeriodLiquidated, scene.PeriodId.ToString()));
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task ApprovingNeedsPeriodApprove()
    {
        var scene = await NewSceneAsync();
        await LiquidatedAsync(scene);
        using var manager = scene.Tenant.Client(PayrollPermissions.PeriodRead, PayrollPermissions.PeriodManage);

        var response = await PostWithVersionAsync(manager, scene, "approve");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task ASettingsOnlyRoleCannotLiquidate()
    {
        var scene = await NewSceneAsync();
        using var settings = scene.Tenant.Client(PayrollPermissions.SettingsManage);

        var response = await PostWithVersionAsync(settings, scene, "liquidate", body: Confirmed, ifMatch: "\"1\"");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        scene.Tenant.Dispose();
    }

    // Review M-2: approve, close and reopen need the version the screen loaded.
    [Fact]
    public async Task ApproveCloseAndReopenNeedAFreshIfMatch()
    {
        var scene = await NewSceneAsync();
        await LiquidatedAsync(scene);
        var reason = new { reason = "Corregir" };

        Assert.Equal((HttpStatusCode)428, (await PostAsync(scene.Tenant.Payroll, $"{scene.PeriodUrl()}/approve", null)).StatusCode);
        Assert.Equal(
            HttpStatusCode.PreconditionFailed,
            (await PostWithVersionAsync(scene.Tenant.Payroll, scene, "approve", ifMatch: "\"999\"")).StatusCode);
        await ApprovedAsync(scene);

        Assert.Equal((HttpStatusCode)428, (await PostAsync(scene.Tenant.Payroll, $"{scene.PeriodUrl()}/close", null)).StatusCode);
        Assert.Equal(
            HttpStatusCode.PreconditionFailed,
            (await PostWithVersionAsync(scene.Tenant.Payroll, scene, "close", ifMatch: "\"999\"")).StatusCode);

        Assert.Equal((HttpStatusCode)428, (await PostAsync(scene.Tenant.Payroll, $"{scene.PeriodUrl()}/reopen", null, reason)).StatusCode);
        Assert.Equal(
            HttpStatusCode.PreconditionFailed,
            (await PostWithVersionAsync(scene.Tenant.Payroll, scene, "reopen", body: reason, ifMatch: "\"999\"")).StatusCode);

        // None of the refused writes changed the period.
        Assert.Equal("Approved", (await DetailAsync(scene.Tenant.Payroll, scene)).GetProperty("period").GetProperty("status").GetString());
        scene.Tenant.Dispose();
    }
}
