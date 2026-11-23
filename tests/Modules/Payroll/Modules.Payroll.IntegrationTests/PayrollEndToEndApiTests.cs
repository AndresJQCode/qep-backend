using System.Net;
using System.Text.Json;
using Modules.Payroll.Application;
using static Modules.Payroll.IntegrationTests.PayrollApiHarness;
using static Modules.Payroll.IntegrationTests.PayrollApiSeeds;

namespace Modules.Payroll.IntegrationTests;

/// <summary>
/// Un mes completo de punta a punta, sólo por la API: dos empleados, uno con comisión y horas extra en la primera quincena. Cada
/// quincena se abre, se liquida, se aprueba y se cierra; la segunda se exporta. El cliente que abre las quincenas tiene
/// <c>payroll.period.manage</c> (I-6).
/// </summary>
public sealed class PayrollEndToEndApiTests : IClassFixture<PayrollApiFixture>
{
    private static readonly Guid ExtraDay = Guid.Parse("00000000-0000-0000-0001-000000000001");
    private static readonly Guid Commissions = Guid.Parse("00000000-0000-0000-0002-000000000001");

    private static readonly DateTimeOffset SecondHalfNow = new(2026, 11, 25, 15, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly PayrollApiFixture _fixture;

    public PayrollEndToEndApiTests(PayrollApiFixture fixture) => _fixture = fixture;

    private static async Task<JsonElement> OkAsync(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode == HttpStatusCode.OK, await BodyAsync(response));
        return await JsonOfAsync(response);
    }

    private static async Task<string> VersionAsync(HttpClient client, string periodUrl) =>
        $"\"{(await OkAsync(await client.GetAsync(periodUrl, Ct))).GetProperty("period").GetProperty("version").GetInt64()}\"";

    private static async Task<JsonElement> TransitionAsync(HttpClient client, string periodUrl, string action, object? body = null) =>
        await OkAsync(await PostAsync(client, $"{periodUrl}/{action}", await VersionAsync(client, periodUrl), body));

    private static decimal AmountOf(JsonElement employeeDetail, string code) =>
        employeeDetail.GetProperty("result").GetProperty("lines").EnumerateArray()
            .Single(line => line.GetProperty("code").GetString() == code).GetProperty("amount").GetDecimal();

    private static List<JsonElement> LinesOf(JsonElement employeeDetail) =>
        employeeDetail.GetProperty("result").GetProperty("lines").EnumerateArray().ToList();

    private static decimal ToPeso(decimal value) => Math.Round(value, 0, MidpointRounding.AwayFromZero);

    private static async Task<Guid> OpenAsync(PayrollTenant tenant, Guid companyId, string half)
    {
        var list = await OkAsync(await tenant.Payroll.PostAsync($"{tenant.Url}/companies/{companyId}/periods/ensure-current", null, Ct));
        return list.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("half").GetString() == half).GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task AWholeMonthFromOpeningToExport()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var company = await SeedCompanyAsync(tenant, "Comercializadora QEP S.A.S.", "Principal", "Bodega", riskClass: "I");
        var area = await CreatedAsync(tenant.Payroll, $"{tenant.HrBase}/areas", new { name = "Operaciones" });
        var position = await CreatedAsync(tenant.Payroll, $"{tenant.HrBase}/positions", new { name = "Analista" });
        var setup = new SeededSetup(
            company.Company, company.SiteId, company.WorkplaceId, area.GetProperty("id").GetGuid(), position.GetProperty("id").GetGuid());
        var ana = EmployeeIdOf(await HireAsync(tenant, HireBody(setup, amount: 3_000_000m)));
        var beto = EmployeeIdOf(await HireAsync(
            tenant, HireBody(setup, amount: 1_750_905m, documentNumber: "1002", firstName: "Beto", lastName: "Ramírez")));

        // First half: capture commission and overtime for Ana, then liquidate, approve and close.
        var first = await OpenAsync(tenant, setup.Company.Id, "First");
        var firstUrl = $"{tenant.Url}/periods/{first}";
        var capture = await PutAsync(
            tenant.Payroll,
            $"{firstUrl}/employees/{ana}/inputs",
            new
            {
                overtime = new[] { new { conceptId = ExtraDay, hours = 4m } },
                incomes = new[] { new { conceptId = Commissions, amount = 400_000m, salaryConstitutive = true } },
                deductions = Array.Empty<object>(),
            },
            await VersionAsync(tenant.Payroll, firstUrl));
        await OkAsync(capture);
        var liquidated = await TransitionAsync(tenant.Payroll, firstUrl, "liquidate", new { confirmNoAbsences = true });
        Assert.Equal(2, liquidated.GetProperty("employees").GetArrayLength());
        Assert.Equal("Approved", (await TransitionAsync(tenant.Payroll, firstUrl, "approve")).GetProperty("period").GetProperty("status").GetString());
        Assert.Equal("Closed", (await TransitionAsync(tenant.Payroll, firstUrl, "close")).GetProperty("period").GetProperty("status").GetString());

        // Second half.
        _fixture.Clock.UtcNow = SecondHalfNow;
        var second = await OpenAsync(tenant, setup.Company.Id, "Second");
        var secondUrl = $"{tenant.Url}/periods/{second}";
        await TransitionAsync(tenant.Payroll, secondUrl, "liquidate", new { confirmNoAbsences = true });
        await TransitionAsync(tenant.Payroll, secondUrl, "approve");
        Assert.Equal("Closed", (await TransitionAsync(tenant.Payroll, secondUrl, "close")).GetProperty("period").GetProperty("status").GetString());

        var export = await tenant.Payroll.GetAsync($"{secondUrl}/export", Ct);
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.NotEmpty(await export.Content.ReadAsByteArrayAsync(Ct));

        foreach (var (employeeId, salary) in new[] { (ana, 3_000_000m), (beto, 1_750_905m) })
        {
            var inFirst = await OkAsync(await tenant.Payroll.GetAsync($"{firstUrl}/employees/{employeeId}", Ct));
            var inSecond = await OkAsync(await tenant.Payroll.GetAsync($"{secondUrl}/employees/{employeeId}", Ct));

            // The salary lines of the two halves add up to the monthly salary, to the peso.
            Assert.Equal(salary, AmountOf(inFirst, "salary") + AmountOf(inSecond, "salary"));

            // The IBC of the month is the salary plus the constitutive earnings captured in the first half.
            var constitutive = LinesOf(inFirst)
                .Where(line => line.GetProperty("code").GetString() is { } code && (code.StartsWith("overtime:", StringComparison.Ordinal) || code.StartsWith("income:", StringComparison.Ordinal)))
                .Sum(line => line.GetProperty("amount").GetDecimal());
            var monthlyIbc = salary + constitutive;
            Assert.Equal(
                ToPeso(monthlyIbc * 0.04m),
                AmountOf(inFirst, "health_employee") + AmountOf(inSecond, "health_employee"));
        }

        // Ana really had overtime and commission: the check above is not trivially the salary.
        var anaFirst = await OkAsync(await tenant.Payroll.GetAsync($"{firstUrl}/employees/{ana}", Ct));
        Assert.Contains(LinesOf(anaFirst), line => line.GetProperty("code").GetString()!.StartsWith("overtime:", StringComparison.Ordinal));
        Assert.Contains(LinesOf(anaFirst), line => line.GetProperty("code").GetString()!.StartsWith("income:", StringComparison.Ordinal));
    }
}
