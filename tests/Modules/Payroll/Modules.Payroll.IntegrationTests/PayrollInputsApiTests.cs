using System.Net;
using System.Text.Json;
using Modules.Payroll.Application;
using static Modules.Payroll.IntegrationTests.PayrollApiHarness;
using static Modules.Payroll.IntegrationTests.PayrollApiSeeds;

namespace Modules.Payroll.IntegrationTests;

/// <summary>
/// Capturar lo variable de un empleado en la quincena: reemplaza las tres listas, sube la versión del período y respeta el
/// <c>If-Match</c>. Los empleados se dan de alta por la API de Hr; el reloj del tenant arranca el 2026-11-10 (primera quincena).
/// </summary>
public sealed class PayrollInputsApiTests : IClassFixture<PayrollApiFixture>
{
    internal static readonly Guid ExtraDay = Guid.Parse("00000000-0000-0000-0001-000000000001");
    internal static readonly Guid ExtraNight = Guid.Parse("00000000-0000-0000-0001-000000000002");
    internal static readonly Guid Commissions = Guid.Parse("00000000-0000-0000-0002-000000000001");
    internal static readonly Guid OtherDeduction = Guid.Parse("00000000-0000-0000-0003-000000000001");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly PayrollApiFixture _fixture;

    public PayrollInputsApiTests(PayrollApiFixture fixture) => _fixture = fixture;

    internal sealed record Scene(PayrollTenant Tenant, SeededSetup Setup, Guid PeriodId, Guid EmployeeId)
    {
        public string InputsUrl(Guid? employeeId = null) => $"{Tenant.Url}/periods/{PeriodId}/employees/{employeeId ?? EmployeeId}/inputs";

        public string EmployeeUrl(Guid? employeeId = null) => $"{Tenant.Url}/periods/{PeriodId}/employees/{employeeId ?? EmployeeId}";
    }

    /// <summary>Un tenant con su empresa, un empleado estándar y la primera quincena de noviembre abierta.</summary>
    internal static async Task<Scene> NewSceneAsync(PayrollApiFixture fixture)
    {
        var tenant = await NewTenantAsync(fixture);
        var setup = await SeedSetupAsync(tenant);
        var employee = await HireAsync(tenant, setup);
        var ensured = await tenant.Payroll.PostAsync($"{tenant.Url}/companies/{setup.Company.Id}/periods/ensure-current", null, Ct);
        Assert.Equal(HttpStatusCode.OK, ensured.StatusCode);
        var periodId = (await JsonOfAsync(ensured)).GetProperty("items")[0].GetProperty("id").GetGuid();
        return new Scene(tenant, setup, periodId, EmployeeIdOf(employee));
    }

    internal static async Task<long> VersionOfAsync(Scene scene)
    {
        var response = await scene.Tenant.Payroll.GetAsync($"{scene.Tenant.Url}/periods/{scene.PeriodId}", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await JsonOfAsync(response)).GetProperty("period").GetProperty("version").GetInt64();
    }

    internal static object InputsBody(
        object[]? overtime = null, object[]? incomes = null, object[]? deductions = null) =>
        new { overtime = overtime ?? [], incomes = incomes ?? [], deductions = deductions ?? [] };

    internal static Task<HttpResponseMessage> PutInputsAsync(Scene scene, object body, string? ifMatch, HttpClient? client = null, Guid? employeeId = null) =>
        PutAsync(client ?? scene.Tenant.Payroll, scene.InputsUrl(employeeId), body, ifMatch);

    private static async Task<JsonElement> OkAsync(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode == HttpStatusCode.OK, await BodyAsync(response));
        return await JsonOfAsync(response);
    }

    [Fact]
    public async Task CapturingNeedsPeriodManage()
    {
        var scene = await NewSceneAsync(_fixture);
        using var readOnly = scene.Tenant.Client(PayrollPermissions.PeriodRead);
        using var settingsOnly = scene.Tenant.Client(PayrollPermissions.SettingsManage);

        var version = await VersionOfAsync(scene);

        Assert.Equal(HttpStatusCode.Forbidden, (await PutInputsAsync(scene, InputsBody(), $"\"{version}\"", readOnly)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PutInputsAsync(scene, InputsBody(), $"\"{version}\"", settingsOnly)).StatusCode);
        Assert.Equal(version, await VersionOfAsync(scene));
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task CapturingReplacesTheThreeLists()
    {
        var scene = await NewSceneAsync(_fixture);
        var first = InputsBody(
            overtime: [new { conceptId = ExtraDay, hours = 4.5m }],
            incomes: [new { conceptId = Commissions, amount = 500_000m, salaryConstitutive = true }],
            deductions: [new { conceptId = OtherDeduction, amount = 20_000m }]);

        var afterFirst = await OkAsync(await PutInputsAsync(scene, first, $"\"{await VersionOfAsync(scene)}\""));

        var inputs = afterFirst.GetProperty("inputs");
        Assert.Equal(4.5m, inputs.GetProperty("overtime")[0].GetProperty("hours").GetDecimal());
        Assert.Equal("Hora extra diurna", inputs.GetProperty("overtime")[0].GetProperty("conceptName").GetString());
        Assert.Equal(500_000m, inputs.GetProperty("incomes")[0].GetProperty("amount").GetDecimal());
        Assert.True(inputs.GetProperty("incomes")[0].GetProperty("salaryConstitutive").GetBoolean());
        Assert.Equal(20_000m, inputs.GetProperty("deductions")[0].GetProperty("amount").GetDecimal());
        Assert.True(afterFirst.GetProperty("employee").GetProperty("hasInputs").GetBoolean());

        var second = InputsBody(deductions: [new { conceptId = OtherDeduction, amount = 7_000m }]);
        var afterSecond = await OkAsync(await PutInputsAsync(scene, second, $"\"{await VersionOfAsync(scene)}\""));

        var replaced = afterSecond.GetProperty("inputs");
        Assert.Equal(0, replaced.GetProperty("overtime").GetArrayLength());
        Assert.Equal(0, replaced.GetProperty("incomes").GetArrayLength());
        Assert.Equal(7_000m, Assert.Single(replaced.GetProperty("deductions").EnumerateArray()).GetProperty("amount").GetDecimal());

        var read = await OkAsync(await scene.Tenant.Payroll.GetAsync(scene.EmployeeUrl(), Ct));
        Assert.Equal(1, read.GetProperty("inputs").GetProperty("deductions").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, read.GetProperty("result").ValueKind);
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task TheAnswerCarriesTheNewVersion()
    {
        var scene = await NewSceneAsync(_fixture);
        var before = await VersionOfAsync(scene);

        var answer = await OkAsync(await PutInputsAsync(scene, InputsBody(), $"\"{before}\""));

        Assert.Equal(before + 1, answer.GetProperty("period").GetProperty("version").GetInt64());
        Assert.Equal(before + 1, await VersionOfAsync(scene));
        scene.Tenant.Dispose();
    }

    // Review Focus 5: dos capturas con el mismo If-Match, la segunda es 412 y sus líneas no quedan.
    [Fact]
    public async Task AStaleIfMatchIs412AndWritesNothing()
    {
        var scene = await NewSceneAsync(_fixture);
        var version = await VersionOfAsync(scene);
        var winner = InputsBody(deductions: [new { conceptId = OtherDeduction, amount = 1_000m }]);
        var loser = InputsBody(deductions: [new { conceptId = OtherDeduction, amount = 9_999m }]);

        await OkAsync(await PutInputsAsync(scene, winner, $"\"{version}\""));
        var stale = await PutInputsAsync(scene, loser, $"\"{version}\"");

        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        var read = await OkAsync(await scene.Tenant.Payroll.GetAsync(scene.EmployeeUrl(), Ct));
        Assert.Equal(1_000m, read.GetProperty("inputs").GetProperty("deductions")[0].GetProperty("amount").GetDecimal());
        Assert.Equal(version + 1, await VersionOfAsync(scene));

        var missing = await PutInputsAsync(scene, winner, null);
        Assert.Equal((HttpStatusCode)428, missing.StatusCode);
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task AnExcludedEmployeeIs422NotLiquidable()
    {
        var scene = await NewSceneAsync(_fixture);
        var integral = EmployeeIdOf(await HireAsync(
            scene.Tenant,
            HireBody(scene.Setup, salaryType: "Integral", amount: 25_000_000m, documentNumber: "1002", firstName: "Beto", lastName: "Ramírez")));

        var response = await PutInputsAsync(
            scene, InputsBody(), $"\"{await VersionOfAsync(scene)}\"", employeeId: integral);

        Assert.Equal((HttpStatusCode)422, response.StatusCode);
        Assert.Equal("payroll.employee.not_liquidable", CodeOf(await JsonOfAsync(response)));
        scene.Tenant.Dispose();
    }

    // I-13: un excluido no es 404; es una fila sin capturas ni resultado.
    [Fact]
    public async Task AnExcludedEmployeeDetailIsEmptyNot404()
    {
        var scene = await NewSceneAsync(_fixture);
        var integral = EmployeeIdOf(await HireAsync(
            scene.Tenant,
            HireBody(scene.Setup, salaryType: "Integral", amount: 25_000_000m, documentNumber: "1002", firstName: "Beto", lastName: "Ramírez")));

        var detail = await OkAsync(await scene.Tenant.Payroll.GetAsync(scene.EmployeeUrl(integral), Ct));

        Assert.Equal(integral, detail.GetProperty("employee").GetProperty("employeeId").GetGuid());
        Assert.Equal("Beto Ramírez", detail.GetProperty("employee").GetProperty("fullName").GetString());
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("result").ValueKind);
        Assert.Equal(0, detail.GetProperty("inputs").GetProperty("overtime").GetArrayLength());
        Assert.Equal(0, detail.GetProperty("recurring").GetArrayLength());
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task AnEmployeeOutsideThePeriodIs404()
    {
        var scene = await NewSceneAsync(_fixture);
        var stranger = Guid.NewGuid();

        var put = await PutInputsAsync(scene, InputsBody(), $"\"{await VersionOfAsync(scene)}\"", employeeId: stranger);
        var get = await scene.Tenant.Payroll.GetAsync(scene.EmployeeUrl(stranger), Ct);

        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
        Assert.Equal("payroll.employee.not_in_period", CodeOf(await JsonOfAsync(put)));
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal("payroll.employee.not_in_period", CodeOf(await JsonOfAsync(get)));
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task AConceptOfAnotherKindIs422OnTheField()
    {
        var scene = await NewSceneAsync(_fixture);

        var response = await PutInputsAsync(
            scene,
            InputsBody(overtime: [new { conceptId = Commissions, hours = 2m }]),
            $"\"{await VersionOfAsync(scene)}\"");

        Assert.Equal((HttpStatusCode)422, response.StatusCode);
        Assert.True(HasError(await JsonOfAsync(response), "overtime[0].conceptId"));
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task AnArchivedConceptIs422()
    {
        var scene = await NewSceneAsync(_fixture);
        var concept = await CreatedAsync(
            scene.Tenant.Payroll, $"{scene.Tenant.Url}/companies/{scene.Setup.Company.Id}/concepts",
            new { kind = "Income", name = "Bono viejo", defaultSalaryConstitutive = (bool?)null, multiplier = (decimal?)null });
        var conceptId = concept.GetProperty("id").GetGuid();
        var archive = await scene.Tenant.Payroll.PostAsync(
            $"{scene.Tenant.Url}/companies/{scene.Setup.Company.Id}/concepts/{conceptId}/archive", null, Ct);
        Assert.Equal(HttpStatusCode.OK, archive.StatusCode);

        var response = await PutInputsAsync(
            scene,
            InputsBody(incomes: [new { conceptId, amount = 1_000m, salaryConstitutive = false }]),
            $"\"{await VersionOfAsync(scene)}\"");

        Assert.Equal((HttpStatusCode)422, response.StatusCode);
        Assert.True(HasError(await JsonOfAsync(response), "incomes[0].conceptId"));
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task AnArchivedConceptAlreadyCapturedCanBeSavedAgain()
    {
        var scene = await NewSceneAsync(_fixture);
        var concept = await CreatedAsync(
            scene.Tenant.Payroll, $"{scene.Tenant.Url}/companies/{scene.Setup.Company.Id}/concepts",
            new { kind = "Income", name = "Bono viejo", defaultSalaryConstitutive = (bool?)null, multiplier = (decimal?)null });
        var conceptId = concept.GetProperty("id").GetGuid();
        var saved = await PutInputsAsync(
            scene, InputsBody(incomes: [new { conceptId, amount = 1_000m, salaryConstitutive = false }]), $"\"{await VersionOfAsync(scene)}\"");
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var archive = await scene.Tenant.Payroll.PostAsync(
            $"{scene.Tenant.Url}/companies/{scene.Setup.Company.Id}/concepts/{conceptId}/archive", null, Ct);
        Assert.Equal(HttpStatusCode.OK, archive.StatusCode);

        // The same line, with another amount: it was already there, so it is kept.
        var again = await PutInputsAsync(
            scene, InputsBody(incomes: [new { conceptId, amount = 2_000m, salaryConstitutive = false }]), $"\"{await VersionOfAsync(scene)}\"");
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);

        // A second line of it is new: rejected on its own field.
        var extra = await PutInputsAsync(
            scene,
            InputsBody(incomes:
            [
                new { conceptId, amount = 2_000m, salaryConstitutive = false },
                new { conceptId, amount = 500m, salaryConstitutive = false },
            ]),
            $"\"{await VersionOfAsync(scene)}\"");
        Assert.Equal((HttpStatusCode)422, extra.StatusCode);
        var problem = await JsonOfAsync(extra);
        Assert.True(HasError(problem, "incomes[1].conceptId"));
        Assert.False(HasError(problem, "incomes[0].conceptId"));
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task ACustomConceptOfAnotherCompanyIs422()
    {
        var scene = await NewSceneAsync(_fixture);
        var other = await SeedSecondCompanyAsync(scene.Tenant);
        var foreign = await CreatedAsync(
            scene.Tenant.Payroll, $"{scene.Tenant.Url}/companies/{other.Company.Id}/concepts",
            new { kind = "Deduction", name = "Libranza ajena", defaultSalaryConstitutive = (bool?)null, multiplier = (decimal?)null });

        var response = await PutInputsAsync(
            scene,
            InputsBody(deductions: [new { conceptId = foreign.GetProperty("id").GetGuid(), amount = 1_000m }]),
            $"\"{await VersionOfAsync(scene)}\"");

        Assert.Equal((HttpStatusCode)422, response.StatusCode);
        Assert.True(HasError(await JsonOfAsync(response), "deductions[0].conceptId"));
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task ABodyOutOfShapeIs422WithItsFields()
    {
        var scene = await NewSceneAsync(_fixture);

        var response = await PutInputsAsync(
            scene,
            InputsBody(
                overtime: [new { conceptId = ExtraDay, hours = 400m }, new { conceptId = ExtraDay, hours = 1m }],
                incomes: [new { conceptId = Commissions, amount = 5m, salaryConstitutive = (bool?)null }]),
            $"\"{await VersionOfAsync(scene)}\"");

        Assert.Equal((HttpStatusCode)422, response.StatusCode);
        var problem = await JsonOfAsync(response);
        Assert.True(HasError(problem, "overtime[0].hours"));
        Assert.True(HasError(problem, "overtime[1].conceptId"));
        Assert.True(HasError(problem, "incomes[0].salaryConstitutive"));
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task CapturingIsAudited()
    {
        var scene = await NewSceneAsync(_fixture);

        await OkAsync(await PutInputsAsync(
            scene,
            InputsBody(incomes: [new { conceptId = Commissions, amount = 123_456m, salaryConstitutive = true }]),
            $"\"{await VersionOfAsync(scene)}\""));

        Assert.Equal(1, await CountAuditEntriesAsync(_fixture.Factory, PayrollAuditActions.InputsUpdated, scene.PeriodId.ToString()));
        scene.Tenant.Dispose();
    }
}
