using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Payroll.Application;
using Modules.Payroll.Domain;
using Modules.Payroll.Domain.Engine;
using static Modules.Payroll.IntegrationTests.PayrollApiHarness;
using static Modules.Payroll.IntegrationTests.PayrollApiSeeds;

namespace Modules.Payroll.IntegrationTests;

/// <summary>
/// El planificador con vacaciones guardadas de verdad: lo que lee de Hr (días de descanso), de la base (vacaciones) y de las
/// quincenas. Se queda en 2026: liquidar 2027 exige cargar sus valores legales anuales.
/// </summary>
public sealed class PayrollVacationPlannerTests : IClassFixture<PayrollApiFixture>
{
    private static readonly DateTimeOffset SecondOfDecember = new(2026, 12, 2, 15, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly PayrollApiFixture _fixture;

    public PayrollVacationPlannerTests(PayrollApiFixture fixture) => _fixture = fixture;

    private sealed record Scene(PayrollTenant Tenant, SeededSetup Setup, Guid EmployeeId, Guid PeriodId)
    {
        public string PeriodUrl => $"{Tenant.Url}/periods/{PeriodId}";
    }

    // An employee with Saturday and Sunday as rest days and the first half of December open (the clock is on the 2nd).
    private async Task<Scene> NewSceneAsync(string startDate = "2025-01-01")
    {
        var tenant = await NewTenantAsync(_fixture);
        var company = await SeedCompanyAsync(tenant, "Comercializadora QEP S.A.S.", "Principal", "Bodega", riskClass: "I");
        var area = await CreatedAsync(tenant.Payroll, $"{tenant.HrBase}/areas", new { name = "Operaciones" });
        var position = await CreatedAsync(tenant.Payroll, $"{tenant.HrBase}/positions", new { name = "Analista" });
        var setup = new SeededSetup(
            company.Company, company.SiteId, company.WorkplaceId, area.GetProperty("id").GetGuid(), position.GetProperty("id").GetGuid());
        var employee = await HireAsync(
            tenant,
            HireBody(setup, startDate: startDate, amount: 3_000_000m, restDays: ["Saturday", "Sunday"]));

        _fixture.Clock.UtcNow = SecondOfDecember;
        var ensured = await tenant.Payroll.PostAsync($"{tenant.Url}/companies/{setup.Company.Id}/periods/ensure-current", null, Ct);
        Assert.True(ensured.StatusCode == HttpStatusCode.OK, await BodyAsync(ensured));
        var periodId = (await JsonOfAsync(ensured)).GetProperty("items")[0].GetProperty("id").GetGuid();
        return new Scene(tenant, setup, EmployeeIdOf(employee), periodId);
    }

    private async Task SaveVacationAsync(Scene scene, DateOnly start, DateOnly end)
    {
        await using var scope = _fixture.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IVacationRepository>().Add(Vacation.Create(
            VacationId.New(), scene.Tenant.Id, scene.Setup.Company.Id, scene.EmployeeId, VacationOrigin.Qep, start, end, 8,
            end.AddDays(3), null, [], PayrollApiFixture.DefaultNow));
        await scope.ServiceProvider.GetRequiredService<IPayrollUnitOfWork>().SaveChangesAsync(Ct);
    }

    private async Task<LiquidationPlan> PlanAsync(Scene scene)
    {
        await using var scope = _fixture.Factory.Services.CreateAsyncScope();
        var period = await scope.ServiceProvider.GetRequiredService<IPayrollPeriodRepository>()
            .FindAsync(scene.Tenant.Id, new PayrollPeriodId(scene.PeriodId), Ct);
        return await scope.ServiceProvider.GetRequiredService<LiquidationPlanner>().PlanAsync(period!, Ct);
    }

    private static async Task<HttpResponseMessage> LiquidateAsync(Scene scene)
    {
        var detail = await scene.Tenant.Payroll.GetAsync(scene.PeriodUrl, Ct);
        var version = (await JsonOfAsync(detail)).GetProperty("period").GetProperty("version").GetInt64();
        return await PostAsync(scene.Tenant.Payroll, $"{scene.PeriodUrl}/liquidate", $"\"{version}\"", new { confirmNoAbsences = true });
    }

    [Fact]
    public async Task PlansTheVacationOfTheMonth()
    {
        var scene = await NewSceneAsync();
        await SaveVacationAsync(scene, new DateOnly(2026, 12, 14), new DateOnly(2026, 12, 24));

        var plan = await PlanAsync(scene);

        var planned = Assert.Single(plan.Liquidable);
        var input = Assert.Single(planned.Input.Vacations);
        Assert.True(input.PaidHere);
        Assert.Equal(VacationOrigin.Qep, input.Origin);
        Assert.Equal((new DateOnly(2026, 12, 14), new DateOnly(2026, 12, 24), 8), (input.StartDate, input.EndDate, input.BusinessDays));
        Assert.Equal(3_000_000m, input.SalaryAtStart);
        // No November in QEP and nothing typed: the salary of the day before the start stands in for the IBC.
        Assert.Equal((3_000_000m, VacationIbcSource.Salary), (input.PreviousMonthIbc, input.PreviousIbcSource));
        Assert.Empty(plan.Unpaid);
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task APeriodWithoutVacationsPlansNone()
    {
        var scene = await NewSceneAsync();

        var plan = await PlanAsync(scene);

        Assert.Empty(Assert.Single(plan.Liquidable).Input.Vacations);
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task LiquidatingSavesThePhoto()
    {
        var scene = await NewSceneAsync();
        await SaveVacationAsync(scene, new DateOnly(2026, 12, 14), new DateOnly(2026, 12, 24));

        var response = await LiquidateAsync(scene);

        Assert.True(response.StatusCode == HttpStatusCode.OK, await BodyAsync(response));
        await using var scope = _fixture.Factory.Services.CreateAsyncScope();
        var stored = await scope.ServiceProvider.GetRequiredService<IPayrollResultStore>()
            .FindAsync(scene.Tenant.Id, new PayrollPeriodId(scene.PeriodId), scene.EmployeeId, Ct);
        Assert.NotNull(stored);
        Assert.Equal(2, stored.Liquidation.VacationDays);
        var photo = Assert.Single(stored.Liquidation.VacationPhotos);
        Assert.Equal(
            [(new DateOnly(2026, 12, 1), 2), (new DateOnly(2026, 12, 16), 9)],
            photo.Quincenas.Select(item => (item.Start, item.Days)));
        Assert.Equal(
            [new VacationMonthAdvance(new DateOnly(2026, 12, 1), 900_000m, 36_000m, 36_000m, 0m)],
            photo.Months);
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task AVacationOutsideTheContractBlocksTheEmployee()
    {
        // The contract starts on the 10th (Hr corrected it after the vacation was registered from the 5th).
        var scene = await NewSceneAsync(startDate: "2026-12-10");
        await SaveVacationAsync(scene, new DateOnly(2026, 12, 5), new DateOnly(2026, 12, 12));

        var response = await LiquidateAsync(scene);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("payroll.liquidation.blocked", CodeOf(await JsonOfAsync(response)));
        var detail = await JsonOfAsync(await scene.Tenant.Payroll.GetAsync(scene.PeriodUrl, Ct));
        var blocked = Assert.Single(detail.GetProperty("blocked").EnumerateArray());
        Assert.Equal(["VacationOutsideContract"], blocked.GetProperty("reasons").EnumerateArray().Select(reason => reason.GetString()));
        scene.Tenant.Dispose();
    }
}
