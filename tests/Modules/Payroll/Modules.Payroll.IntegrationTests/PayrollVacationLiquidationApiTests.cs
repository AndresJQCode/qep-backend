using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modules.Payroll.Application;
using Modules.Payroll.Domain;
using Npgsql;
using static Modules.Payroll.IntegrationTests.PayrollApiHarness;
using static Modules.Payroll.IntegrationTests.PayrollApiSeeds;

namespace Modules.Payroll.IntegrationTests;

/// <summary>
/// Liquidar con vacaciones pagadas en otra quincena, de punta a punta: la espera de la que paga (regla 5), lo que pasa con las
/// siguientes al volver a liquidarla (regla 4) y el ejemplo que cruza de mes. Enero de 2027 necesita sus valores legales anuales: se
/// copian los vigentes de 2026 (los mismos del caso de oro sintético) a 2027-01-01.
/// </summary>
public sealed class PayrollVacationLiquidationApiTests : IClassFixture<PayrollApiFixture>
{
    private static readonly DateTimeOffset SecondOfDecember = new(2026, 12, 2, 15, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset TwentyFifthOfDecember = new(2026, 12, 25, 15, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset TenthOfJanuary = new(2027, 1, 10, 15, 0, 0, TimeSpan.Zero);
    private static readonly SemaphoreSlim SeedGate = new(1, 1);
    private static bool _seeded;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly PayrollApiFixture _fixture;

    public PayrollVacationLiquidationApiTests(PayrollApiFixture fixture) => _fixture = fixture;

    private sealed record Scene(PayrollTenant Tenant, SeededSetup Setup, Guid EmployeeId, Guid ContractId);

    private static string PeriodsUrl(Scene scene) => $"{scene.Tenant.Url}/companies/{scene.Setup.Company.Id}/periods";

    private static string VacationsUrl(Scene scene) =>
        $"{scene.Tenant.Url}/companies/{scene.Setup.Company.Id}/employees/{scene.EmployeeId}/vacations";

    // 2027 has its own yearly legal values (PayrollLegalValues): the rows in force in 2026 are copied to the 1st of January, which is
    // what the synthetic golden case of January assumes (SMMLV 1 750 905, transport 249 095).
    private async Task EnsureLegalValuesOf2027Async()
    {
        await SeedGate.WaitAsync(Ct);
        try
        {
            if (_seeded)
            {
                return;
            }

            var connectionString = _fixture.Factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("QepDatabase")!;
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand(
                """
                INSERT INTO hr.legal_parameters (key, effective_from, value, updated_at, updated_by)
                SELECT DISTINCT ON (key) key, DATE '2027-01-01', value, now(), updated_by
                FROM hr.legal_parameters ORDER BY key, effective_from DESC
                ON CONFLICT DO NOTHING;
                INSERT INTO hr.legal_parameter_brackets (key, effective_from, lower_bound, upper_bound, rate, updated_at, updated_by)
                SELECT key, DATE '2027-01-01', lower_bound, upper_bound, rate, now(), updated_by
                FROM hr.legal_parameter_brackets b
                WHERE effective_from = (SELECT max(effective_from) FROM hr.legal_parameter_brackets WHERE key = b.key)
                ON CONFLICT DO NOTHING;
                """,
                connection);
            await command.ExecuteNonQueryAsync(Ct);
            _seeded = true;
        }
        finally
        {
            SeedGate.Release();
        }
    }

    // An employee with Saturday and Sunday as rest days at 3 000 000 and the first half of December open (the clock is on the 2nd).
    private async Task<Scene> NewSceneAsync()
    {
        await EnsureLegalValuesOf2027Async();
        var tenant = await NewTenantAsync(_fixture);
        var company = await SeedCompanyAsync(tenant, "Comercializadora QEP S.A.S.", "Principal", "Bodega", riskClass: "I");
        var area = await CreatedAsync(tenant.Payroll, $"{tenant.HrBase}/areas", new { name = "Operaciones" });
        var position = await CreatedAsync(tenant.Payroll, $"{tenant.HrBase}/positions", new { name = "Analista" });
        var setup = new SeededSetup(
            company.Company, company.SiteId, company.WorkplaceId, area.GetProperty("id").GetGuid(), position.GetProperty("id").GetGuid());
        var employee = await HireAsync(
            tenant, HireBody(setup, startDate: "2025-01-01", amount: 3_000_000m, restDays: ["Saturday", "Sunday"]));
        var scene = new Scene(
            tenant, setup, EmployeeIdOf(employee), employee.GetProperty("contract").GetProperty("id").GetGuid());
        await OpenPeriodsAsync(scene, SecondOfDecember);
        return scene;
    }

    // Moves the clock and opens whatever quincena is missing up to that day; answers the id of each by its start date.
    private async Task<Dictionary<string, Guid>> OpenPeriodsAsync(Scene scene, DateTimeOffset now)
    {
        _fixture.Clock.UtcNow = now;
        var response = await scene.Tenant.Payroll.PostAsync($"{PeriodsUrl(scene)}/ensure-current", null, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await BodyAsync(response));
        return (await JsonOfAsync(response)).GetProperty("items").EnumerateArray()
            .ToDictionary(item => item.GetProperty("startDate").GetString()!, item => item.GetProperty("id").GetGuid());
    }

    private static async Task<Guid> PeriodIdAsync(Scene scene, string start) =>
        (await JsonOfAsync(await scene.Tenant.Payroll.GetAsync($"{scene.Tenant.Url}/companies/{scene.Setup.Company.Id}/periods", Ct)))
            .GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("startDate").GetString() == start).GetProperty("id").GetGuid();

    private static async Task<JsonElement> PeriodAsync(Scene scene, Guid periodId) =>
        (await OkAsync(await scene.Tenant.Payroll.GetAsync($"{scene.Tenant.Url}/periods/{periodId}", Ct))).GetProperty("period");

    private static async Task<JsonElement> OkAsync(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode == HttpStatusCode.OK, await BodyAsync(response));
        return await JsonOfAsync(response);
    }

    private static async Task<HttpResponseMessage> PostActionAsync(Scene scene, Guid periodId, string action, object? body = null)
    {
        var version = (await PeriodAsync(scene, periodId)).GetProperty("version").GetInt64();
        return await PostAsync(scene.Tenant.Payroll, $"{scene.Tenant.Url}/periods/{periodId}/{action}", $"\"{version}\"", body);
    }

    private static Task<HttpResponseMessage> TryLiquidateAsync(Scene scene, Guid periodId) =>
        PostActionAsync(scene, periodId, "liquidate", new { confirmNoAbsences = true });

    private static async Task LiquidateAsync(Scene scene, Guid periodId) => await OkAsync(await TryLiquidateAsync(scene, periodId));

    private static async Task ApproveAsync(Scene scene, Guid periodId) => await OkAsync(await PostActionAsync(scene, periodId, "approve"));

    private static async Task ReopenAsync(Scene scene, Guid periodId) =>
        await OkAsync(await PostActionAsync(scene, periodId, "reopen", new { reason = "Corregir" }));

    private static async Task<JsonElement> RejectedAsync(HttpResponseMessage response, string code)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await JsonOfAsync(response);
        Assert.Equal(code, CodeOf(problem));
        return problem;
    }

    private static async Task<JsonElement> CreateVacationAsync(Scene scene, object body)
    {
        var response = await scene.Tenant.Payroll.PostAsJsonAsync(VacationsUrl(scene), body, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await BodyAsync(response));
        return (await JsonOfAsync(response))[0];
    }

    private static object DecemberToJanuary => new { origin = "Qep", startDate = "2026-12-14", endDate = "2027-01-05" };

    private static async Task<HttpResponseMessage> UpdateVacationAsync(Scene scene, JsonElement vacation, object body) =>
        await PutAsync(
            scene.Tenant.Payroll, $"{VacationsUrl(scene)}/{vacation.GetProperty("id").GetGuid()}", body,
            $"\"{vacation.GetProperty("version").GetInt64()}\"");

    private static async Task<JsonElement> EmployeeAsync(Scene scene, Guid periodId) =>
        await OkAsync(await scene.Tenant.Payroll.GetAsync($"{scene.Tenant.Url}/periods/{periodId}/employees/{scene.EmployeeId}", Ct));

    private static decimal AmountOf(JsonElement employee, string code) => employee.GetProperty("result").GetProperty("lines")
        .EnumerateArray().Single(line => line.GetProperty("code").GetString() == code).GetProperty("amount").GetDecimal();

    private static bool HasLine(JsonElement employee, string startsWith) => employee.GetProperty("result").GetProperty("lines")
        .EnumerateArray().Any(line => line.GetProperty("code").GetString()!.StartsWith(startsWith, StringComparison.Ordinal));

    private static async Task RaiseSalaryAsync(Scene scene, string effectiveDate)
    {
        var response = await scene.Tenant.Payroll.PostAsJsonAsync(
            $"{scene.Tenant.HrBase}/employees/{scene.EmployeeId}/contracts/{scene.ContractId}/salaries",
            new { effectiveDate, amount = 3_200_000m },
            Ct);
        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created, await BodyAsync(response));
    }

    // A vacation row written straight through the repository: no outdating, as if the quincena had been liquidated before it existed.
    private async Task SaveVacationRowAsync(Scene scene, DateOnly start, DateOnly end)
    {
        await using var scope = _fixture.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IVacationRepository>().Add(Vacation.Create(
            VacationId.New(), scene.Tenant.Id, scene.Setup.Company.Id, scene.EmployeeId, VacationOrigin.Qep, start, end, 15,
            end.AddDays(1), null, [], PayrollApiFixture.DefaultNow));
        await scope.ServiceProvider.GetRequiredService<IPayrollUnitOfWork>().SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task TheSecondHalfWaitsForThePayingOne()
    {
        var scene = await NewSceneAsync();
        await CreateVacationAsync(scene, DecemberToJanuary);
        var periods = await OpenPeriodsAsync(scene, TwentyFifthOfDecember);

        // Today's rule: the second half needs the first one liquidated.
        await RejectedAsync(await TryLiquidateAsync(scene, periods["2026-12-16"]), "payroll.period.first_half_not_liquidated");

        // The first half is liquidated, but Hr moved something afterwards: it is not «al día», so it does not count as paid.
        await LiquidateAsync(scene, periods["2026-12-01"]);
        await RaiseSalaryAsync(scene, "2026-12-05");
        var problem = await RejectedAsync(await TryLiquidateAsync(scene, periods["2026-12-16"]), "payroll.vacation.not_paid_yet");
        Assert.Contains("del 1 al 15 de diciembre de 2026", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task JanuaryNeedsDecemberPaid()
    {
        var scene = await NewSceneAsync();
        await CreateVacationAsync(scene, DecemberToJanuary);
        var periods = await OpenPeriodsAsync(scene, TenthOfJanuary);

        // December is still a Draft.
        var problem = await RejectedAsync(await TryLiquidateAsync(scene, periods["2027-01-01"]), "payroll.vacation.not_paid_yet");

        Assert.Contains("del 1 al 15 de diciembre de 2026", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task AnOutdatedPayingQuincenaDoesNotPay()
    {
        var scene = await NewSceneAsync();
        var vacation = await CreateVacationAsync(scene, DecemberToJanuary);
        await LiquidateAsync(scene, await PeriodIdAsync(scene, "2026-12-01"));
        var edit = await UpdateVacationAsync(scene, vacation, new { origin = "Qep", startDate = "2026-12-14", endDate = "2027-01-06" });
        await OkAsync(edit);
        var periods = await OpenPeriodsAsync(scene, TenthOfJanuary);

        await RejectedAsync(await TryLiquidateAsync(scene, periods["2027-01-01"]), "payroll.vacation.not_paid_yet");
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task AReopenedPayingQuincenaDoesNotPay()
    {
        var scene = await NewSceneAsync();
        await CreateVacationAsync(scene, DecemberToJanuary);
        var december = await PeriodIdAsync(scene, "2026-12-01");
        await LiquidateAsync(scene, december);
        await ApproveAsync(scene, december);
        await ReopenAsync(scene, december);
        var periods = await OpenPeriodsAsync(scene, TenthOfJanuary);

        await RejectedAsync(await TryLiquidateAsync(scene, periods["2027-01-01"]), "payroll.vacation.not_paid_yet");
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task APayingQuincenaWithoutThePhotoDoesNotPay()
    {
        var scene = await NewSceneAsync();

        // December is liquidated before the vacation exists, so its results carry no photo of it.
        await LiquidateAsync(scene, await PeriodIdAsync(scene, "2026-12-01"));
        await SaveVacationRowAsync(scene, new DateOnly(2026, 12, 14), new DateOnly(2027, 1, 5));
        var periods = await OpenPeriodsAsync(scene, TenthOfJanuary);

        await RejectedAsync(await TryLiquidateAsync(scene, periods["2027-01-01"]), "payroll.vacation.not_paid_yet");
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task TheWholeExampleClosesTheMonth()
    {
        var scene = await NewSceneAsync();
        var vacation = await CreateVacationAsync(scene, DecemberToJanuary);
        var vacationId = vacation.GetProperty("id").GetGuid();
        var december = await PeriodIdAsync(scene, "2026-12-01");
        await LiquidateAsync(scene, december);
        var periods = await OpenPeriodsAsync(scene, TwentyFifthOfDecember);
        await LiquidateAsync(scene, periods["2026-12-16"]);
        periods = await OpenPeriodsAsync(scene, TenthOfJanuary);
        await LiquidateAsync(scene, periods["2027-01-01"]);

        // vacation-crosses-month-paid-first-half-1: pays all 22 payroll days and withholds ahead what the other days will owe.
        var first = await EmployeeAsync(scene, december);
        Assert.Equal(1_300_000m, AmountOf(first, "salary"));
        Assert.Equal(2_200_000m, AmountOf(first, $"vacation_pay:{vacationId}"));
        Assert.Equal(60_000m, AmountOf(first, "health_employee"));
        Assert.Equal(60_000m, AmountOf(first, "pension_employee"));
        Assert.Equal(80_000m, AmountOf(first, "health_employee_vacation_advance"));
        Assert.Equal(80_000m, AmountOf(first, "pension_employee_vacation_advance"));
        Assert.Equal(1_500_000m, AmountOf(first, "ibc"));

        // …-2: a whole quincena of vacation owes nothing: the advance of the first one covers it.
        var second = await EmployeeAsync(scene, periods["2026-12-16"]);
        Assert.Equal(0m, AmountOf(second, "salary"));
        Assert.Equal(0m, AmountOf(second, "health_employee"));
        Assert.Equal(0m, AmountOf(second, "pension_employee"));
        Assert.Equal(3_000_000m, AmountOf(second, "ibc"));
        Assert.False(HasLine(second, "vacation_pay"));

        // …-3: January pays the 10 days worked and withholds only what the advance did not.
        var january = await EmployeeAsync(scene, periods["2027-01-01"]);
        Assert.Equal(1_000_000m, AmountOf(january, "salary"));
        Assert.Equal(83_032m, AmountOf(january, "transport_allowance"));
        Assert.Equal(40_000m, AmountOf(january, "health_employee"));
        Assert.Equal(40_000m, AmountOf(january, "pension_employee"));
        Assert.Equal(1_500_000m, AmountOf(january, "ibc"));
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task ReliquidatingThePayingOneOutdatesTheFollowing()
    {
        var scene = await NewSceneAsync();
        await CreateVacationAsync(scene, DecemberToJanuary);
        var december = await PeriodIdAsync(scene, "2026-12-01");
        await LiquidateAsync(scene, december);
        var periods = await OpenPeriodsAsync(scene, TenthOfJanuary);
        await LiquidateAsync(scene, periods["2027-01-01"]);
        Assert.False((await PeriodAsync(scene, periods["2027-01-01"])).GetProperty("outdated").GetBoolean());

        await LiquidateAsync(scene, december);

        Assert.True((await PeriodAsync(scene, periods["2027-01-01"])).GetProperty("outdated").GetBoolean());
        scene.Tenant.Dispose();
    }

    // Approved second half of December, then the first one reopened: what the second half read is what a new liquidation decides.
    private async Task<(Scene Scene, Guid First)> ApprovedSecondWithTheFirstReopenedAsync()
    {
        var scene = await NewSceneAsync();
        await CreateVacationAsync(scene, DecemberToJanuary);
        var first = await PeriodIdAsync(scene, "2026-12-01");
        await LiquidateAsync(scene, first);
        await ApproveAsync(scene, first);
        var periods = await OpenPeriodsAsync(scene, TwentyFifthOfDecember);
        await LiquidateAsync(scene, periods["2026-12-16"]);
        await ApproveAsync(scene, periods["2026-12-16"]);
        await ReopenAsync(scene, first);
        return (scene, first);
    }

    [Fact]
    public async Task ReliquidatingThePayingOneIsLockedByAnApprovedFollowing()
    {
        var (scene, first) = await ApprovedSecondWithTheFirstReopenedAsync();

        // A raise on the 5th changes the salary the vacation is paid with, so the photo the approved second half reads would change.
        await RaiseSalaryAsync(scene, "2026-12-05");
        var problem = await RejectedAsync(await TryLiquidateAsync(scene, first), "payroll.vacation.paid_period_locked");

        Assert.Contains("del 16 al 31 de diciembre de 2026", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task ReliquidatingThePayingOneButPassesWhenThePhotoIsTheSame()
    {
        var (scene, first) = await ApprovedSecondWithTheFirstReopenedAsync();

        var response = await TryLiquidateAsync(scene, first);

        Assert.True(response.StatusCode == HttpStatusCode.OK, await BodyAsync(response));
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task ExternalDoesNotWaitForAnyPayment()
    {
        var scene = await NewSceneAsync();
        await CreateVacationAsync(scene, new
        {
            origin = "External",
            startDate = "2026-11-23",
            endDate = "2026-12-04",
            previousMonthIbc = 3_000_000m,
            businessDays = 10,
            externalMonths = new[] { new { month = "2026-12", healthAdvance = 16_000m, pensionAdvance = 16_000m } },
        });
        var december = await PeriodIdAsync(scene, "2026-12-01");

        // vacation-external-november-into-december-advance: nobody pays this in QEP, and what Siigo withheld ahead is subtracted.
        await LiquidateAsync(scene, december);

        var employee = await EmployeeAsync(scene, december);
        Assert.Equal(1_100_000m, AmountOf(employee, "salary"));
        Assert.False(HasLine(employee, "vacation_pay"));
        Assert.Equal(44_000m, AmountOf(employee, "health_employee"));
        Assert.Equal(44_000m, AmountOf(employee, "pension_employee"));
        scene.Tenant.Dispose();
    }
}
