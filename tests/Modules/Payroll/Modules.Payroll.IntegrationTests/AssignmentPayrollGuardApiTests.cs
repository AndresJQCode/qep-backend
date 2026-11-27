using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Payroll.Application;
using static Modules.Payroll.IntegrationTests.PayrollApiHarness;
using static Modules.Payroll.IntegrationTests.PayrollApiSeeds;
using static Modules.Payroll.IntegrationTests.PayrollExportApiTests;

namespace Modules.Payroll.IntegrationTests;

/// <summary>
/// Editar y eliminar registros de cargo y lugar contra la nómina (spec 2026-11-26, «Nómina»): sólo un período <c>Approved</c> o
/// <c>Closed</c> con resultado guardado del empleado bloquea. Reloj del harness: 2026-11-10, quincena 2026-11-01..15.
/// </summary>
public sealed class AssignmentPayrollGuardApiTests : IClassFixture<PayrollApiFixture>
{
    private static readonly DateOnly FirstHalfStart = new(2026, 11, 1);
    private static readonly DateOnly FirstHalfEnd = new(2026, 11, 15);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly PayrollApiFixture _fixture;

    public AssignmentPayrollGuardApiTests(PayrollApiFixture fixture) => _fixture = fixture;

    private async Task<IReadOnlyList<PayrollFrozenPeriod>> FrozenAsync(Guid tenantId, Guid employeeId)
    {
        await using var scope = _fixture.Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IPayrollFrozenPeriods>()
            .ListWithResultOfAsync(tenantId, employeeId, Ct);
    }

    [Fact]
    public async Task OnlyApprovedOrClosedPeriodsWithAResultAreListed()
    {
        var scene = await NewSceneAsync(_fixture);
        var expected = new PayrollFrozenPeriod(scene.Setup.Company.Id, FirstHalfStart, FirstHalfEnd);

        await TransitionAsync(scene, "liquidate", new { confirmNoAbsences = true });
        Assert.Empty(await FrozenAsync(scene.Tenant.Id, scene.EmployeeId));

        await TransitionAsync(scene, "approve");
        Assert.Equal([expected], await FrozenAsync(scene.Tenant.Id, scene.EmployeeId));

        await TransitionAsync(scene, "close");
        Assert.Equal([expected], await FrozenAsync(scene.Tenant.Id, scene.EmployeeId));

        Assert.Empty(await FrozenAsync(Guid.NewGuid(), scene.EmployeeId));
    }

    private static string ContractUrl(Scene scene, JsonElement detail) =>
        $"{scene.Tenant.HrBase}/employees/{scene.EmployeeId}/contracts/{detail.GetProperty("contract").GetProperty("id").GetGuid()}";

    private static string Version(JsonElement detail) => $"\"{detail.GetProperty("employee").GetProperty("version").GetInt64()}\"";

    private static JsonElement RecordAt(JsonElement detail, string effectiveDate) =>
        detail.GetProperty("contract").GetProperty("assignments").EnumerateArray()
            .Single(record => record.GetProperty("effectiveDate").GetString() == effectiveDate);

    private static (bool CanEdit, bool CanDelete) FlagsOf(JsonElement record) =>
        (record.GetProperty("canEdit").GetBoolean(), record.GetProperty("canDelete").GetBoolean());

    private static async Task<JsonElement> OkAsync(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode == HttpStatusCode.OK, await BodyAsync(response));
        return await JsonOfAsync(response);
    }

    private static async Task<string> ProblemCodeAsync(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, await BodyAsync(response));
        return CodeOf(await JsonOfAsync(response));
    }

    private static async Task<HttpResponseMessage> DeleteAsync(HttpClient client, string url, string ifMatch)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, url);
        request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        return await client.SendAsync(request, Ct);
    }

    // De punta a punta, sin dobles: opción (a) del owner. Un Liquidated no bloquea; un Approved con resultado del empleado sí.
    [Fact]
    public async Task ALiquidatedPeriodDoesNotBlockAndAnApprovedOneDoes()
    {
        var scene = await NewSceneAsync(_fixture);
        var client = scene.Tenant.Payroll;
        var coordinator = (await CreatedAsync(client, $"{scene.Tenant.HrBase}/positions", new { name = "Coordinador" })).GetProperty("id").GetGuid();
        var sales = (await CreatedAsync(client, $"{scene.Tenant.HrBase}/areas", new { name = "Ventas" })).GetProperty("id").GetGuid();
        var hired = await OkAsync(await client.GetAsync($"{scene.Tenant.HrBase}/employees/{scene.EmployeeId}", Ct));
        var url = ContractUrl(scene, hired);
        var added = await OkAsync(await client.PostAsJsonAsync(
            $"{url}/assignments",
            new { effectiveDate = "2026-11-05", positionId = coordinator, areaId = scene.Setup.AreaId, workplaceId = scene.Setup.WorkplaceId },
            Ct));
        var recordId = RecordAt(added, "2026-11-05").GetProperty("id").GetGuid();

        await TransitionAsync(scene, "liquidate", new { confirmNoAbsences = true });
        var rewritten = await OkAsync(await PutAsync(
            client,
            $"{url}/assignments/{recordId}",
            new { effectiveDate = "2026-11-05", positionId = coordinator, areaId = sales, workplaceId = scene.Setup.WorkplaceId, confirmRewrite = true },
            Version(added)));
        await TransitionAsync(scene, "approve");

        var approved = await OkAsync(await client.GetAsync($"{scene.Tenant.HrBase}/employees/{scene.EmployeeId}", Ct));
        Assert.Equal((false, false), FlagsOf(RecordAt(approved, "2026-11-05")));
        Assert.Equal((false, false), FlagsOf(RecordAt(approved, "2025-01-01")));
        Assert.Equal(
            "hr.assignment.used_by_payroll",
            await ProblemCodeAsync(await PutAsync(
                client,
                $"{url}/assignments/{recordId}",
                new { effectiveDate = "2026-11-05", positionId = coordinator, areaId = scene.Setup.AreaId, workplaceId = scene.Setup.WorkplaceId, confirmRewrite = true },
                Version(approved))));
        Assert.Equal(
            "hr.assignment.used_by_payroll",
            await ProblemCodeAsync(await DeleteAsync(client, $"{url}/assignments/{recordId}", Version(approved))));
        Assert.Equal(Version(rewritten), Version(approved));

        var later = await OkAsync(await client.PostAsJsonAsync(
            $"{url}/assignments",
            new { effectiveDate = "2026-12-01", positionId = scene.Setup.PositionId, areaId = sales, workplaceId = scene.Setup.WorkplaceId },
            Ct));
        var laterRecord = RecordAt(later, "2026-12-01");
        Assert.Equal((true, true), FlagsOf(laterRecord));
        await OkAsync(await DeleteAsync(client, $"{url}/assignments/{laterRecord.GetProperty("id").GetGuid()}", Version(later)));
    }

    [Fact]
    public async Task AnEmployeeWithoutAResultIsNotListed()
    {
        var scene = await NewSceneAsync(_fixture);
        await TransitionAsync(scene, "liquidate", new { confirmNoAbsences = true });
        await TransitionAsync(scene, "approve");

        var later = EmployeeIdOf(await HireAsync(scene.Tenant, scene.Setup, "3003"));

        Assert.Empty(await FrozenAsync(scene.Tenant.Id, later));
    }
}
