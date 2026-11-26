using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Hr.Application;
using Modules.Hr.Domain;
using Modules.Tenancy.Application;
using Testcontainers.PostgreSql;
using static Modules.Hr.IntegrationTests.HrApiHarness;

namespace Modules.Hr.IntegrationTests;

/// <summary>
/// La consola de valores legales del operador de punta a punta (spec 2026-11-22, «API»). El tenant de pruebas hace de operador
/// (<c>Platform:OperatorTenantId</c>), el uso por nóminas calculadas es un doble con sellos que la prueba fija, y «hoy» lo manda un
/// reloj movible.
/// </summary>
public sealed class LegalParameterOperatorApiTests
{
    private static readonly DateTimeOffset NovemberThirtieth = new(2026, 11, 30, 15, 0, 0, TimeSpan.Zero);

    private static readonly Guid OperatorTenant = Guid.Parse(TenantId);

    private static readonly string[] ReadAndManage =
        [HrOperatorPermissions.LegalParametersRead, HrOperatorPermissions.LegalParametersManage];

    private static string Base(string tenantId = TenantId) => $"/api/v1/tenants/{tenantId}/operator/legal-parameters";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Un doble de las nóminas calculadas: la prueba fija los sellos y el adaptador real de Payroll no entra.</summary>
    private sealed class FakeLegalParameterUsage : ILegalParameterUsage
    {
        public List<CalculatedPeriodStamp> Stamps { get; } = [];

        public Task<IReadOnlyList<CalculatedPeriodStamp>> ListCalculatedPeriodsOfAllTenantsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CalculatedPeriodStamp>>(Stamps.ToList());
    }

    private sealed record World(
        PostgreSqlContainer Database, QepApiFactory Factory, HttpClient Client, FakeLegalParameterUsage Usage, MutableTenantClock Clock)
        : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            Factory.Dispose();
            await Database.DisposeAsync();
        }
    }

    private static async Task<World> OpenAsync(DateTimeOffset now, params string[] permissions)
    {
        var database = await StartDatabaseAsync();
        var clock = new MutableTenantClock(now);
        var usage = new FakeLegalParameterUsage();
        var factory = new QepApiFactory(
            database.GetConnectionString(),
            services =>
            {
                services.RemoveAll<ITenantClock>();
                services.AddSingleton<ITenantClock>(clock);
                services.RemoveAll<ILegalParameterUsage>();
                services.AddSingleton<ILegalParameterUsage>(usage);
            },
            OperatorTenant);
        await SeedTenantAsync(factory);
        var client = CreateClient(factory, SubjectId, TenantId, permissions.Length == 0 ? ReadAndManage : permissions);
        return new World(database, factory, client, usage, clock);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement;
    }

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == status, $"{(int)response.StatusCode}: {body}");
        var json = JsonDocument.Parse(body).RootElement;
        Assert.Equal(code, json.GetProperty("code").GetString());
        return json;
    }

    private static JsonElement Parameter(JsonElement root, string key) =>
        root.GetProperty("parameters").EnumerateArray().Single(item => item.GetProperty("key").GetString() == key);

    [Fact]
    public async Task WithoutReadIs403AndNonOperatorTenantIs403()
    {
        await using var world = await OpenAsync(NovemberThirtieth, "tenancy.settings.read");
        await SeedTenantAsync(world.Factory, OtherTenantId);
        using var other = CreateClient(world.Factory, SubjectId, OtherTenantId, ReadAndManage);

        var withoutPermission = await world.Client.GetAsync(Base() + "/", Ct);
        var put = await world.Client.PutAsJsonAsync($"{Base()}/Smmlv/2027-01-01", new { value = 2_000_000 }, Ct);
        var history = await world.Client.GetAsync(Base() + "/history", Ct);
        var nonOperator = await other.GetAsync(Base(OtherTenantId) + "/", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, withoutPermission.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, history.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, nonOperator.StatusCode);
    }

    [Fact]
    public async Task GetReturnsThirtyKeysWithNameGroupAndUnit()
    {
        await using var world = await OpenAsync(NovemberThirtieth);

        var root = await JsonAsync(await world.Client.GetAsync(Base() + "/", Ct));

        Assert.Equal("2026-11-30", root.GetProperty("today").GetString());
        Assert.Equal(2026, root.GetProperty("currentYear").GetInt32());
        Assert.Equal(2027, root.GetProperty("nextYear").GetInt32());
        Assert.Equal(30, root.GetProperty("parameters").GetArrayLength());
        var smmlv = Parameter(root, "Smmlv");
        Assert.Equal("Salario mínimo mensual legal vigente", smmlv.GetProperty("name").GetString());
        Assert.Equal("SalaryAndCaps", smmlv.GetProperty("group").GetString());
        Assert.Equal("Currency", smmlv.GetProperty("unit").GetString());
        Assert.True(smmlv.GetProperty("yearly").GetBoolean());
        var current = smmlv.GetProperty("current");
        Assert.Equal("2026-01-01", current.GetProperty("effectiveFrom").GetString());
        Assert.Equal(1_750_905m, current.GetProperty("value").GetDecimal());
        var rows = smmlv.GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal(
            Enumerable.Range(2017, 10).Reverse().Select(year => $"{year}-01-01"),
            rows.Select(row => row.GetProperty("effectiveFrom").GetString()));
        Assert.All(rows, row => Assert.Equal(JsonValueKind.Null, row.GetProperty("updatedAt").ValueKind));
        Assert.False(Parameter(root, "WeeklyHours").GetProperty("yearly").GetBoolean());
        var brackets = Assert.Single(root.GetProperty("brackets").EnumerateArray());
        Assert.Equal("SolidarityFund", brackets.GetProperty("key").GetString());
        Assert.Equal("2026-01-01", brackets.GetProperty("current").GetProperty("effectiveFrom").GetString());
        Assert.Equal(JsonValueKind.Null, brackets.GetProperty("sets")[0].GetProperty("updatedAt").ValueKind);
    }

    [Fact]
    public async Task NextYearMissingOnlyFromDecember()
    {
        await using var world = await OpenAsync(NovemberThirtieth);

        var november = await JsonAsync(await world.Client.GetAsync(Base() + "/", Ct));
        world.Clock.UtcNow = new DateTimeOffset(2026, 12, 1, 15, 0, 0, TimeSpan.Zero);
        var december = await JsonAsync(await world.Client.GetAsync(Base() + "/", Ct));

        Assert.Equal(0, november.GetProperty("nextYearMissingCount").GetInt32());
        Assert.All(
            november.GetProperty("parameters").EnumerateArray(),
            item => Assert.False(item.GetProperty("nextYearMissing").GetBoolean()));
        Assert.Equal(28, december.GetProperty("nextYearMissingCount").GetInt32());
        Assert.True(Parameter(december, "Smmlv").GetProperty("nextYearMissing").GetBoolean());
        Assert.False(Parameter(december, "WeeklyHours").GetProperty("nextYearMissing").GetBoolean());
        Assert.True(december.GetProperty("brackets")[0].GetProperty("nextYearMissing").GetBoolean());
    }

    [Fact]
    public async Task CurrentYearMissingCountsTheYearWithoutJanuaryFirst()
    {
        await using var world = await OpenAsync(new DateTimeOffset(2027, 3, 10, 15, 0, 0, TimeSpan.Zero));

        var root = await JsonAsync(await world.Client.GetAsync(Base() + "/", Ct));

        Assert.Equal(28, root.GetProperty("currentYearMissingCount").GetInt32());
        Assert.True(Parameter(root, "Smmlv").GetProperty("currentYearMissing").GetBoolean());
        Assert.False(Parameter(root, "WeeklyHours").GetProperty("currentYearMissing").GetBoolean());
        Assert.True(root.GetProperty("brackets")[0].GetProperty("currentYearMissing").GetBoolean());
    }

    [Fact]
    public async Task PutCreatesAndHistoryShowsManual()
    {
        await using var world = await OpenAsync(NovemberThirtieth);

        var put = await JsonAsync(await world.Client.PutAsJsonAsync($"{Base()}/Smmlv/2027-01-01", new { value = 2_000_000 }, Ct));
        var history = await JsonAsync(await world.Client.GetAsync(Base() + "/history", Ct));

        var created = Parameter(put, "Smmlv").GetProperty("rows")[0];
        Assert.Equal("2027-01-01", created.GetProperty("effectiveFrom").GetString());
        Assert.Equal(2_000_000m, created.GetProperty("value").GetDecimal());
        Assert.NotEqual(JsonValueKind.Null, created.GetProperty("updatedAt").ValueKind);
        Assert.Equal(1, history.GetProperty("total").GetInt32());
        Assert.Equal(1, history.GetProperty("page").GetInt32());
        Assert.Equal(50, history.GetProperty("pageSize").GetInt32());
        var item = history.GetProperty("items")[0];
        Assert.Equal("Created", item.GetProperty("action").GetString());
        Assert.Equal("Parameter", item.GetProperty("kind").GetString());
        Assert.Equal("Smmlv", item.GetProperty("key").GetString());
        Assert.Equal("2027-01-01", item.GetProperty("effectiveFrom").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("oldValue").ValueKind);
        Assert.Equal(2_000_000m, item.GetProperty("newValue").GetDecimal());
        Assert.Equal("Manual", item.GetProperty("source").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("batchId").ValueKind);
        Assert.Equal(Guid.Parse(SubjectId), item.GetProperty("changedBy").GetProperty("subjectId").GetGuid());
    }

    [Fact]
    public async Task PutInUseIs409WithCounts()
    {
        await using var world = await OpenAsync(NovemberThirtieth);
        world.Usage.Stamps.Add(new CalculatedPeriodStamp(new DateOnly(2026, 1, 16), Guid.NewGuid()));

        var response = await world.Client.PutAsJsonAsync($"{Base()}/Smmlv/2026-01-10", new { value = 1_800_000 }, Ct);

        var problem = await ProblemAsync(response, HttpStatusCode.Conflict, "hr.legal_parameter.in_use");
        Assert.Equal(1, problem.GetProperty("periodsAffected").GetInt32());
        Assert.Equal(1, problem.GetProperty("companiesAffected").GetInt32());
        Assert.Equal("2026-01-16", problem.GetProperty("from").GetString());
        Assert.Equal("2026-01-16", problem.GetProperty("to").GetString());
    }

    [Theory]
    [InlineData("smmlv")]
    [InlineData("0")]
    [InlineData("Foo")]
    public async Task UnknownKeyIs404(string key)
    {
        await using var world = await OpenAsync(NovemberThirtieth);

        var put = await world.Client.PutAsJsonAsync($"{Base()}/{key}/2027-01-01", new { value = 2_000_000 }, Ct);
        var delete = await world.Client.DeleteAsync($"{Base()}/{key}/2027-01-01", Ct);
        var brackets = await world.Client.DeleteAsync($"{Base()}/brackets/{key}/2027-01-01", Ct);

        await ProblemAsync(put, HttpStatusCode.NotFound, "hr.legal_parameter.key_unknown");
        await ProblemAsync(delete, HttpStatusCode.NotFound, "hr.legal_parameter.key_unknown");
        await ProblemAsync(brackets, HttpStatusCode.NotFound, "hr.legal_parameter.key_unknown");
    }

    [Fact]
    public async Task BadDateIs422()
    {
        await using var world = await OpenAsync(NovemberThirtieth);

        var response = await world.Client.PutAsJsonAsync($"{Base()}/Smmlv/2026-13-01", new { value = 2_000_000 }, Ct);

        await ProblemAsync(response, HttpStatusCode.UnprocessableEntity, "hr.legal_parameter.date_invalid");
    }

    [Fact]
    public async Task RateAboveOneIs422ValueInvalid()
    {
        await using var world = await OpenAsync(NovemberThirtieth);

        var response = await world.Client.PutAsJsonAsync($"{Base()}/HealthEmployeeRate/2027-01-01", new { value = 12 }, Ct);
        var missingBody = await world.Client.PutAsJsonAsync($"{Base()}/Smmlv/2027-01-01", new { }, Ct);

        await ProblemAsync(response, HttpStatusCode.UnprocessableEntity, "hr.legal_parameter.value_invalid");
        await ProblemAsync(missingBody, HttpStatusCode.UnprocessableEntity, "hr.legal_parameter.value_invalid");
    }

    [Fact]
    public async Task DeletingTheOnlyRowIsLastRow()
    {
        await using var world = await OpenAsync(NovemberThirtieth);

        // ConnectivityAllowanceSince sólo tiene la fila sembrada del 25 de junio de 2025.
        var response = await world.Client.DeleteAsync($"{Base()}/ConnectivityAllowanceSince/2025-06-25", Ct);

        await ProblemAsync(response, HttpStatusCode.UnprocessableEntity, "hr.legal_parameter.last_row");
    }

    [Fact]
    public async Task DeleteRemovesTheRowAndRecordsIt()
    {
        await using var world = await OpenAsync(NovemberThirtieth);
        await JsonAsync(await world.Client.PutAsJsonAsync($"{Base()}/Smmlv/2027-01-01", new { value = 2_000_000 }, Ct));

        var deleted = await JsonAsync(await world.Client.DeleteAsync($"{Base()}/Smmlv/2027-01-01", Ct));
        var history = await JsonAsync(await world.Client.GetAsync(Base() + "/history?page=1&pageSize=1", Ct));

        Assert.DoesNotContain(
            Parameter(deleted, "Smmlv").GetProperty("rows").EnumerateArray(),
            row => row.GetProperty("effectiveFrom").GetString() == "2027-01-01");
        Assert.Equal(2, history.GetProperty("total").GetInt32());
        Assert.Equal(1, history.GetProperty("pageSize").GetInt32());
        var item = Assert.Single(history.GetProperty("items").EnumerateArray());
        Assert.Equal("Deleted", item.GetProperty("action").GetString());
        Assert.Equal(2_000_000m, item.GetProperty("oldValue").GetDecimal());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("newValue").ValueKind);
    }

    [Fact]
    public async Task AHugePageIsClampedAndAnswersEmptyInsteadOfOverflowing()
    {
        await using var world = await OpenAsync(NovemberThirtieth);

        var history = await JsonAsync(await world.Client.GetAsync(Base() + "/history?page=2147483647&pageSize=100", Ct));

        Assert.Empty(history.GetProperty("items").EnumerateArray());
        Assert.Equal(100, history.GetProperty("pageSize").GetInt32());
        Assert.True(history.GetProperty("page").GetInt32() >= 1);
    }

    [Fact]
    public async Task PutBracketsReplacesTheSet()
    {
        await using var world = await OpenAsync(NovemberThirtieth);
        var body = new
        {
            brackets = new object[]
            {
                new { lowerBound = 4, upperBound = 16, rate = 0.01 },
                new { lowerBound = 16, upperBound = (decimal?)null, rate = 0.02 },
            },
        };

        var put = await JsonAsync(await world.Client.PutAsJsonAsync($"{Base()}/brackets/SolidarityFund/2027-01-01", body, Ct));
        var history = await JsonAsync(await world.Client.GetAsync(Base() + "/history", Ct));

        var set = put.GetProperty("brackets")[0].GetProperty("sets")[0];
        Assert.Equal("2027-01-01", set.GetProperty("effectiveFrom").GetString());
        Assert.NotEqual(JsonValueKind.Null, set.GetProperty("updatedAt").ValueKind);
        Assert.Equal(2, set.GetProperty("brackets").GetArrayLength());
        var item = history.GetProperty("items")[0];
        Assert.Equal("Brackets", item.GetProperty("kind").GetString());
        Assert.Equal("SolidarityFund", item.GetProperty("key").GetString());
        Assert.Equal("Created", item.GetProperty("action").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("newValue").ValueKind);
        var newBrackets = item.GetProperty("newBrackets").EnumerateArray().ToList();
        Assert.Equal(2, newBrackets.Count);
        Assert.Equal(0.02m, newBrackets[1].GetProperty("rate").GetDecimal());
        Assert.Equal(JsonValueKind.Null, newBrackets[1].GetProperty("upperBound").ValueKind);
    }

    [Fact]
    public async Task NonContiguousBracketsAre422()
    {
        await using var world = await OpenAsync(NovemberThirtieth);
        var body = new
        {
            brackets = new object[]
            {
                new { lowerBound = 4, upperBound = 16, rate = 0.01 },
                new { lowerBound = 17, upperBound = (decimal?)null, rate = 0.02 },
            },
        };

        var response = await world.Client.PutAsJsonAsync($"{Base()}/brackets/SolidarityFund/2027-01-01", body, Ct);
        var empty = await world.Client.PutAsJsonAsync($"{Base()}/brackets/SolidarityFund/2027-01-01", new { }, Ct);

        await ProblemAsync(response, HttpStatusCode.UnprocessableEntity, "hr.legal_parameter.value_invalid");
        await ProblemAsync(empty, HttpStatusCode.UnprocessableEntity, "hr.legal_parameter.value_invalid");
    }

    [Fact]
    public async Task TheSalaryRulesStopFailingAfterCreatingTheMissingYear()
    {
        await using var world = await OpenAsync(NovemberThirtieth, [.. ReadAndManage, HrPermissions.ContractRead]);
        var rules = $"{HrUrl()}/salary-rules?date=2016-06-01";

        var before = await world.Client.GetAsync(rules, Ct);
        await JsonAsync(await world.Client.PutAsJsonAsync($"{Base()}/Smmlv/2016-01-01", new { value = 689_455 }, Ct));
        await JsonAsync(await world.Client.PutAsJsonAsync($"{Base()}/IntegralSalaryFloor/2016-01-01", new { value = 8_962_915 }, Ct));
        var after = await world.Client.GetAsync(rules, Ct);

        await ProblemAsync(before, HttpStatusCode.UnprocessableEntity, "hr.legal_parameter.missing");
        var body = await JsonAsync(after);
        Assert.Equal(689_455m, body.GetProperty("smmlv").GetDecimal());
    }
}
