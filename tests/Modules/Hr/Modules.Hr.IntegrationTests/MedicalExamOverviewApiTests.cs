using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Hr.Application;
using static Modules.Hr.IntegrationTests.HrApiHarness;
using static Modules.Hr.IntegrationTests.HrApiSeeds;

namespace Modules.Hr.IntegrationTests;

/// <summary>
/// Spec 2026-11-23 §7.2: the control overview. The counts and the status of each employee are decided by the backend, the page is
/// cut in the database, and the order is urgency first. The clock of the fixture is 2026-11-20 (Friday); the tenant is Colombian.
/// </summary>
public sealed class MedicalExamOverviewApiTests : IClassFixture<HrApiFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly HrApiFixture _fixture;

    public MedicalExamOverviewApiTests(HrApiFixture fixture) => _fixture = fixture;

    private sealed record World(HrTenant Tenant, Dictionary<string, JsonElement> People);

    private static string ContractUrl(HrTenant tenant, JsonElement detail) =>
        $"{EmployeeUrl(tenant, detail)}/contracts/{detail.GetProperty("contract").GetProperty("id").GetGuid()}";

    private static async Task RecordAsync(HrTenant tenant, JsonElement detail, string type, string date)
    {
        var response = await tenant.Hr.PostAsJsonAsync(
            $"{EmployeeUrl(tenant, detail)}/medical-exams",
            new { type, examDate = date, provider = (string?)null, restrictions = (string?)null, recommendations = (string?)null, restrictionsUntil = (string?)null },
            Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<JsonElement> HireNamedAsync(
        HrTenant tenant, SeededSetup setup, string document, string firstName, string lastName, Guid? positionId = null) =>
        await HireAsync(tenant, HireBody(setup, documentNumber: document, firstName: firstName, lastName: lastName, positionId: positionId));

    private static Task<HttpResponseMessage> TerminateAsync(HrTenant tenant, JsonElement detail, string date) =>
        tenant.Hr.PostAsJsonAsync($"{ContractUrl(tenant, detail)}/terminate", new { reason = "Resignation", date, notes = (string?)null }, Ct);

    /// <summary>
    /// Today is 2026-11-20 and every position of the setup is every 12 months. One person per status (two overdue, to see the order
    /// by next due date), plus two outside the universe: a retired one with the exit closed and a deactivated one.
    /// </summary>
    private async Task<World> SeedWorldAsync()
    {
        var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        await PutAsync(tenant.Hr, $"{tenant.Url}/positions/{setup.PositionId}/exam-frequency", new { months = 12 }, "\"1\"");
        var noFrequency = await CreatedAsync(tenant.Hr, $"{tenant.Url}/positions", new { name = "Auxiliar" });
        var people = new Dictionary<string, JsonElement>
        {
            ["noExams"] = await HireNamedAsync(tenant, setup, "7700000001", "Nora", "Aguirre"),
            ["overdue"] = await HireNamedAsync(tenant, setup, "7700000002", "Oscar", "Beltrán"),
            ["overdueOlder"] = await HireNamedAsync(tenant, setup, "7700000003", "Olga", "Cárdenas"),
            ["dueSoon"] = await HireNamedAsync(tenant, setup, "7700000004", "Diego", "Zuluaga"),
            ["upToDate"] = await HireNamedAsync(tenant, setup, "7700000005", "Ursula", "Duarte"),
            ["notRequired"] = await HireNamedAsync(tenant, setup, "7700000006", "Nelson", "Espitia", noFrequency.GetProperty("id").GetGuid()),
            ["exitPending"] = await HireNamedAsync(tenant, setup, "7700000007", "Elena", "Fajardo"),
            ["exitClosed"] = await HireNamedAsync(tenant, setup, "7700000008", "Carlos", "Gaitán"),
            ["deactivated"] = await HireNamedAsync(tenant, setup, "7700000009", "Dora", "Henao"),
        };

        await RecordAsync(tenant, people["overdue"], "Periodic", "2025-01-10");
        await RecordAsync(tenant, people["overdueOlder"], "Periodic", "2024-06-01");
        await RecordAsync(tenant, people["dueSoon"], "Periodic", "2025-12-10");
        await RecordAsync(tenant, people["upToDate"], "Periodic", "2026-10-01");
        await RecordAsync(tenant, people["notRequired"], "PreEmployment", "2026-02-01");
        await TerminateAsync(tenant, people["exitPending"], "2026-11-16");
        await TerminateAsync(tenant, people["exitClosed"], "2026-11-02");
        await RecordAsync(tenant, people["exitClosed"], "Exit", "2026-11-04");
        await tenant.Hr.PostAsJsonAsync($"{EmployeeUrl(tenant, people["deactivated"])}/deactivate", new { date = "2026-11-19" }, Ct);

        return new World(tenant, people);
    }

    private static string IdOf(JsonElement detail) => detail.GetProperty("employee").GetProperty("id").GetString()!;

    private static async Task<JsonElement> OverviewAsync(HrTenant tenant, string query = "", HttpClient? client = null)
    {
        var response = await (client ?? tenant.Hr).GetAsync($"{tenant.Url}/medical-exams/overview{query}", Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await BodyAsync(response));
        return await JsonOfAsync(response);
    }

    private static List<string> EmployeeIds(JsonElement overview) =>
        overview.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("employeeId").GetString()!).ToList();

    [Fact]
    public async Task OverviewCountsIgnoreTheFilters()
    {
        var world = await SeedWorldAsync();
        using var _ = world.Tenant;

        var unfiltered = await OverviewAsync(world.Tenant);
        var filtered = await OverviewAsync(world.Tenant, "?status=Overdue&search=Beltr");

        var counts = unfiltered.GetProperty("counts");
        Assert.Equal(
            new Dictionary<string, int>
            {
                ["ExitPending"] = 1,
                ["NoExams"] = 1,
                ["Overdue"] = 2,
                ["DueSoon"] = 1,
                ["UpToDate"] = 1,
                ["NotRequired"] = 1,
            },
            counts.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.GetInt32()));
        Assert.Equal(counts.GetRawText(), filtered.GetProperty("counts").GetRawText());
        Assert.Equal(1, filtered.GetProperty("total").GetInt32());
        Assert.Equal(7, unfiltered.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task OverviewStatusListFiltersWithComma()
    {
        var world = await SeedWorldAsync();
        using var _ = world.Tenant;

        var overview = await OverviewAsync(world.Tenant, "?status=UpToDate,NotRequired");

        Assert.Equal(
            [IdOf(world.People["upToDate"]), IdOf(world.People["notRequired"])],
            EmployeeIds(overview));
        Assert.Equal(2, overview.GetProperty("total").GetInt32());
        Assert.Equal(["UpToDate", "NotRequired"], overview.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("status").GetString()));
    }

    [Fact]
    public async Task OverviewOrdersByUrgencyThenNextDue()
    {
        var world = await SeedWorldAsync();
        using var _ = world.Tenant;

        var overview = await OverviewAsync(world.Tenant);

        Assert.Equal(
            [
                IdOf(world.People["exitPending"]), IdOf(world.People["noExams"]), IdOf(world.People["overdueOlder"]),
                IdOf(world.People["overdue"]), IdOf(world.People["dueSoon"]), IdOf(world.People["upToDate"]), IdOf(world.People["notRequired"]),
            ],
            EmployeeIds(overview));

        var pending = overview.GetProperty("items")[0];
        Assert.Equal("ExitPending", pending.GetProperty("status").GetString());
        Assert.Equal("2026-11-23", pending.GetProperty("nextDue").GetString());
        Assert.Equal(1, pending.GetProperty("businessDaysLeft").GetInt32());
        Assert.Equal("Elena Fajardo", pending.GetProperty("fullName").GetString());
        Assert.Equal("CC", pending.GetProperty("documentType").GetString());
        Assert.Equal("7700000007", pending.GetProperty("documentNumber").GetString());
        Assert.Equal("Analista", pending.GetProperty("positionName").GetString());

        var overdueOlder = overview.GetProperty("items")[2];
        Assert.Equal("2025-06-01", overdueOlder.GetProperty("nextDue").GetString());
        Assert.Equal(JsonValueKind.Null, overdueOlder.GetProperty("businessDaysLeft").ValueKind);
        Assert.Equal("Periodic", overdueOlder.GetProperty("lastExam").GetProperty("type").GetString());
        Assert.Equal("2024-06-01", overdueOlder.GetProperty("lastExam").GetProperty("examDate").GetString());

        var none = overview.GetProperty("items")[1];
        Assert.Equal(JsonValueKind.Null, none.GetProperty("lastExam").ValueKind);
        Assert.Equal(JsonValueKind.Null, none.GetProperty("nextDue").ValueKind);
        Assert.Equal("Auxiliar", overview.GetProperty("items")[6].GetProperty("positionName").GetString());
        Assert.Equal(JsonValueKind.Null, overview.GetProperty("items")[6].GetProperty("nextDue").ValueKind);
    }

    [Fact]
    public async Task OverviewUniverseIsActivePlusRetiredWithPendingExit()
    {
        var world = await SeedWorldAsync();
        using var _ = world.Tenant;

        var ids = EmployeeIds(await OverviewAsync(world.Tenant));

        Assert.Contains(IdOf(world.People["exitPending"]), ids);
        Assert.DoesNotContain(IdOf(world.People["exitClosed"]), ids);
        Assert.DoesNotContain(IdOf(world.People["deactivated"]), ids);
        Assert.Equal(7, ids.Count);
    }

    [Fact]
    public async Task OverviewSearchMatchesNameOrDocument()
    {
        var world = await SeedWorldAsync();
        using var _ = world.Tenant;

        var byName = await OverviewAsync(world.Tenant, "?search=zuluaga");
        var byDocument = await OverviewAsync(world.Tenant, "?search=7700000005");
        var wildcard = await OverviewAsync(world.Tenant, "?search=%25");

        Assert.Equal([IdOf(world.People["dueSoon"])], EmployeeIds(byName));
        Assert.Equal([IdOf(world.People["upToDate"])], EmployeeIds(byDocument));
        Assert.Empty(EmployeeIds(wildcard));
    }

    [Fact]
    public async Task OverviewUnknownStatusReturns422()
    {
        var world = await SeedWorldAsync();
        using var _ = world.Tenant;

        var response = await world.Tenant.Hr.GetAsync($"{world.Tenant.Url}/medical-exams/overview?status=Overdue,Nonsense", Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("hr.medical_exam.status_invalid", CodeOf(await JsonOfAsync(response)));
        var badPage = await world.Tenant.Hr.GetAsync($"{world.Tenant.Url}/medical-exams/overview?page=abc", Ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badPage.StatusCode);
    }

    [Fact]
    public async Task OverviewWithoutMedicalReadReturns403()
    {
        using var tenant = await NewTenantAsync(_fixture);
        using var withoutMedical = tenant.Client(HrPermissions.EmployeeRead, HrPermissions.ContractRead);
        using var withoutEmployee = tenant.Client(HrPermissions.MedicalRead);

        Assert.Equal(HttpStatusCode.Forbidden, (await withoutMedical.GetAsync($"{tenant.Url}/medical-exams/overview", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await withoutEmployee.GetAsync($"{tenant.Url}/medical-exams/overview", Ct)).StatusCode);
    }

    [Fact]
    public async Task OverviewPaginates20EmployeesPageSize5()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        for (var number = 1; number <= 20; number++)
        {
            await HireNamedAsync(tenant, setup, $"88{number:D8}", $"Persona{number:D2}", $"Apellido{number:D2}");
        }

        var seen = new List<string>();
        for (var page = 1; page <= 4; page++)
        {
            var overview = await OverviewAsync(tenant, $"?page={page}&pageSize=5");
            Assert.Equal(20, overview.GetProperty("total").GetInt32());
            Assert.Equal(page, overview.GetProperty("page").GetInt32());
            Assert.Equal(5, overview.GetProperty("pageSize").GetInt32());
            Assert.Equal(5, EmployeeIds(overview).Count);
            seen.AddRange(overview.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("fullName").GetString()!));
        }

        Assert.Equal(20, seen.Distinct().Count());
        Assert.Equal(seen.OrderBy(name => name[(name.IndexOf(' ') + 1)..], StringComparer.Ordinal), seen);
        Assert.Empty(EmployeeIds(await OverviewAsync(tenant, "?page=5&pageSize=5")));
        Assert.Equal(20, (await OverviewAsync(tenant)).GetProperty("counts").GetProperty("NoExams").GetInt32());
    }
}
