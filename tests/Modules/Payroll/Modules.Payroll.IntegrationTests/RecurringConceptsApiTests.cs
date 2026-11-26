using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Payroll.Application;
using static Modules.Payroll.IntegrationTests.PayrollApiHarness;
using static Modules.Payroll.IntegrationTests.PayrollApiSeeds;
using static Modules.Payroll.IntegrationTests.PayrollInputsApiTests;

namespace Modules.Payroll.IntegrationTests;

/// <summary>
/// Los conceptos recurrentes de la ficha de un empleado. Leer acepta <c>period.read</c> o <c>settings.manage</c>; escribir, sólo
/// <c>settings.manage</c> (decisión del owner, 2026-11-21): las pruebas de permisos van primero.
/// </summary>
public sealed class RecurringConceptsApiTests : IClassFixture<PayrollApiFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly PayrollApiFixture _fixture;

    public RecurringConceptsApiTests(PayrollApiFixture fixture) => _fixture = fixture;

    private static string ListUrl(Scene scene, Guid? employeeId = null, Guid? companyId = null) =>
        $"{scene.Tenant.Url}/companies/{companyId ?? scene.Setup.Company.Id}/employees/{employeeId ?? scene.EmployeeId}/recurring-concepts";

    private static object Body(
        Guid? conceptId = null, string mode = "Fixed", decimal amount = 100_000m, bool? salaryConstitutive = false, decimal? cap = 1_200_000m) =>
        new { conceptId = conceptId ?? OtherIncomeId, mode, amount, salaryConstitutive, cap };

    private static readonly Guid OtherIncomeId = Guid.Parse("00000000-0000-0000-0002-000000000004");

    private static async Task<JsonElement> CreateAsync(Scene scene, object body)
    {
        var response = await scene.Tenant.Payroll.PostAsJsonAsync(ListUrl(scene), body, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await BodyAsync(response));
        return await JsonOfAsync(response);
    }

    private static async Task<JsonElement> OkAsync(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode == HttpStatusCode.OK, await BodyAsync(response));
        return await JsonOfAsync(response);
    }

    // --- Permisos, primero (decisión del owner) ---

    [Fact]
    public async Task ASettingsOnlyRoleReadsAndWritesRecurringConcepts()
    {
        var scene = await NewSceneAsync(_fixture);
        using var settings = scene.Tenant.Client(PayrollPermissions.SettingsManage);

        Assert.Equal(HttpStatusCode.OK, (await settings.GetAsync(ListUrl(scene), Ct)).StatusCode);

        var created = await settings.PostAsJsonAsync(ListUrl(scene), Body(), Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var item = (await JsonOfAsync(created))[0];
        var id = item.GetProperty("id").GetGuid();
        var version = item.GetProperty("version").GetInt64();

        var edited = await PutAsync(settings, $"{ListUrl(scene)}/{id}", Body(amount: 150_000m), $"\"{version}\"");
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await settings.PostAsync($"{ListUrl(scene)}/{id}/deactivate", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await settings.PostAsync($"{ListUrl(scene)}/{id}/activate", null, Ct)).StatusCode);
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task ASettingsOnlyRoleStillCannotReadOrLiquidatePeriods()
    {
        var scene = await NewSceneAsync(_fixture);
        using var settings = scene.Tenant.Client(PayrollPermissions.SettingsManage);
        var version = await VersionOfAsync(scene);

        Assert.Equal(HttpStatusCode.Forbidden, (await settings.GetAsync($"{scene.Tenant.Url}/periods/{scene.PeriodId}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await settings.GetAsync(scene.EmployeeUrl(), Ct)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await PutInputsAsync(scene, InputsBody(), $"\"{version}\"", settings)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await PostAsync(settings, $"{scene.Tenant.Url}/periods/{scene.PeriodId}/liquidate", $"\"{version}\"", new { confirmNoAbsences = true })).StatusCode);
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task AReadOnlyRoleReadsRecurringConceptsButCannotWriteThem()
    {
        var scene = await NewSceneAsync(_fixture);
        var created = (await CreateAsync(scene, Body()))[0];
        var id = created.GetProperty("id").GetGuid();
        using var reader = scene.Tenant.Client(PayrollPermissions.PeriodRead);

        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync(ListUrl(scene), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync(ListUrl(scene), Body(), Ct)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await PutAsync(reader, $"{ListUrl(scene)}/{id}", Body(), $"\"{created.GetProperty("version").GetInt64()}\"")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsync($"{ListUrl(scene)}/{id}/deactivate", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsync($"{ListUrl(scene)}/{id}/activate", null, Ct)).StatusCode);
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task WithoutEitherPermissionRecurringConceptsAre403()
    {
        var scene = await NewSceneAsync(_fixture);
        using var manager = scene.Tenant.Client(PayrollPermissions.PeriodManage);

        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync(ListUrl(scene), Ct)).StatusCode);
        scene.Tenant.Dispose();
    }

    // --- Casos de uso ---

    [Fact]
    public async Task CreatingAnswersTheWholeList()
    {
        var scene = await NewSceneAsync(_fixture);
        await CreateAsync(scene, Body(amount: 100_000m));

        var list = await CreateAsync(scene, Body(conceptId: Guid.Parse("00000000-0000-0000-0003-000000000001"), amount: 30_000m, salaryConstitutive: null, cap: null));

        Assert.Equal(2, list.GetArrayLength());
        var income = list.EnumerateArray().Single(item => item.GetProperty("concept").GetProperty("kind").GetString() == "Income");
        Assert.Equal("Otros ingresos", income.GetProperty("concept").GetProperty("name").GetString());
        Assert.Equal("Fixed", income.GetProperty("mode").GetString());
        Assert.Equal(100_000m, income.GetProperty("amount").GetDecimal());
        Assert.False(income.GetProperty("salaryConstitutive").GetBoolean());
        Assert.Equal("Active", income.GetProperty("status").GetString());
        Assert.Equal(scene.EmployeeId, income.GetProperty("employeeId").GetGuid());
        Assert.Equal(1, income.GetProperty("version").GetInt64());
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task AnOvertimeConceptIsKindMismatch()
    {
        var scene = await NewSceneAsync(_fixture);

        var response = await scene.Tenant.Payroll.PostAsJsonAsync(ListUrl(scene), Body(conceptId: ExtraDay, salaryConstitutive: null), Ct);

        Assert.Equal((HttpStatusCode)422, response.StatusCode);
        Assert.Equal("payroll.concept.kind_mismatch", CodeOf(await JsonOfAsync(response)));
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task ACapBelowTheAmountIs422()
    {
        var scene = await NewSceneAsync(_fixture);

        var response = await scene.Tenant.Payroll.PostAsJsonAsync(ListUrl(scene), Body(amount: 100_000m, cap: 50_000m), Ct);

        Assert.Equal((HttpStatusCode)422, response.StatusCode);
        Assert.True(HasError(await JsonOfAsync(response), "cap"));
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task TheClassificationFollowsTheKindOfTheConcept()
    {
        var scene = await NewSceneAsync(_fixture);

        var incomeWithout = await scene.Tenant.Payroll.PostAsJsonAsync(ListUrl(scene), Body(salaryConstitutive: null), Ct);
        var deductionWith = await scene.Tenant.Payroll.PostAsJsonAsync(
            ListUrl(scene), Body(conceptId: OtherDeduction, salaryConstitutive: true), Ct);

        Assert.Equal((HttpStatusCode)422, incomeWithout.StatusCode);
        Assert.True(HasError(await JsonOfAsync(incomeWithout), "salaryConstitutive"));
        Assert.Equal((HttpStatusCode)422, deductionWith.StatusCode);
        Assert.True(HasError(await JsonOfAsync(deductionWith), "salaryConstitutive"));
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task DeactivatingAndActivatingNeedNoIfMatch()
    {
        var scene = await NewSceneAsync(_fixture);
        var id = (await CreateAsync(scene, Body()))[0].GetProperty("id").GetGuid();

        var off = await OkAsync(await scene.Tenant.Payroll.PostAsync($"{ListUrl(scene)}/{id}/deactivate", null, Ct));
        Assert.Equal("Inactive", off[0].GetProperty("status").GetString());

        var twice = await scene.Tenant.Payroll.PostAsync($"{ListUrl(scene)}/{id}/deactivate", null, Ct);
        Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);
        Assert.Equal("payroll.recurring.already_inactive", CodeOf(await JsonOfAsync(twice)));

        var on = await OkAsync(await scene.Tenant.Payroll.PostAsync($"{ListUrl(scene)}/{id}/activate", null, Ct));
        Assert.Equal("Active", on[0].GetProperty("status").GetString());

        var activeTwice = await scene.Tenant.Payroll.PostAsync($"{ListUrl(scene)}/{id}/activate", null, Ct);
        Assert.Equal(HttpStatusCode.Conflict, activeTwice.StatusCode);
        Assert.Equal("payroll.recurring.already_active", CodeOf(await JsonOfAsync(activeTwice)));
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task EditingNeedsIfMatch()
    {
        var scene = await NewSceneAsync(_fixture);
        var created = (await CreateAsync(scene, Body()))[0];
        var id = created.GetProperty("id").GetGuid();

        var without = await PutAsync(scene.Tenant.Payroll, $"{ListUrl(scene)}/{id}", Body(amount: 120_000m), null);
        Assert.Equal((HttpStatusCode)428, without.StatusCode);

        var edited = await OkAsync(await PutAsync(scene.Tenant.Payroll, $"{ListUrl(scene)}/{id}", Body(amount: 120_000m), "\"1\""));
        Assert.Equal(120_000m, edited[0].GetProperty("amount").GetDecimal());
        Assert.Equal(2, edited[0].GetProperty("version").GetInt64());

        var stale = await PutAsync(scene.Tenant.Payroll, $"{ListUrl(scene)}/{id}", Body(amount: 130_000m), "\"1\"");
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);

        var other = await PutAsync(scene.Tenant.Payroll, $"{ListUrl(scene)}/{Guid.NewGuid()}", Body(), "\"1\"");
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
        Assert.Equal("payroll.recurring.not_found", CodeOf(await JsonOfAsync(other)));
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task AnEmployeeWithoutAContractInTheCompanyIs404()
    {
        var scene = await NewSceneAsync(_fixture);
        var other = await SeedSecondCompanyAsync(scene.Tenant);

        var unknown = await scene.Tenant.Payroll.GetAsync(ListUrl(scene, employeeId: Guid.NewGuid()), Ct);
        var wrongCompany = await scene.Tenant.Payroll.GetAsync(ListUrl(scene, companyId: other.Company.Id), Ct);
        var create = await scene.Tenant.Payroll.PostAsJsonAsync(ListUrl(scene, employeeId: Guid.NewGuid()), Body(), Ct);

        foreach (var response in new[] { unknown, wrongCompany, create })
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("payroll.employee.not_found", CodeOf(await JsonOfAsync(response)));
        }

        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task AccumulatedStartsAtZero()
    {
        var scene = await NewSceneAsync(_fixture);
        var list = await CreateAsync(scene, Body(cap: 1_200_000m));

        var item = list[0];
        Assert.Equal(0m, item.GetProperty("accumulated").GetDecimal());
        Assert.Equal(1_200_000m, item.GetProperty("remaining").GetDecimal());
        Assert.Equal(1_200_000m, item.GetProperty("cap").GetDecimal());
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task RecurringConceptsAppearInTheEmployeeDetail()
    {
        var scene = await NewSceneAsync(_fixture);
        await CreateAsync(scene, Body());

        var detail = await OkAsync(await scene.Tenant.Payroll.GetAsync(scene.EmployeeUrl(), Ct));

        Assert.Equal(1, detail.GetProperty("recurring").GetArrayLength());
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task ChangingTheConceptOfARecurringIs422()
    {
        var scene = await NewSceneAsync(_fixture);
        var created = (await CreateAsync(scene, Body()))[0];

        var response = await PutAsync(
            scene.Tenant.Payroll,
            $"{ListUrl(scene)}/{created.GetProperty("id").GetGuid()}",
            Body(conceptId: Guid.Parse("00000000-0000-0000-0002-000000000001")),
            "\"1\"");

        Assert.Equal((HttpStatusCode)422, response.StatusCode);
        Assert.True(HasError(await JsonOfAsync(response), "conceptId"));
        scene.Tenant.Dispose();
    }
}
