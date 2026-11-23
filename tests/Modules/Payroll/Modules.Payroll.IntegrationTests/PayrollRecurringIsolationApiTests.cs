using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Modules.Payroll.IntegrationTests.PayrollApiHarness;
using static Modules.Payroll.IntegrationTests.PayrollApiSeeds;

namespace Modules.Payroll.IntegrationTests;

/// <summary>
/// Review I-2: every recurring route refuses another tenant (403) and another company or tenant's ids (404), and a cap belongs to
/// one employee: what one employee's recurring applied never eats another employee's.
/// </summary>
public sealed class PayrollRecurringIsolationApiTests : IClassFixture<PayrollApiFixture>
{
    private static readonly Guid OtherIncome = Guid.Parse("00000000-0000-0000-0002-000000000004");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly PayrollApiFixture _fixture;

    public PayrollRecurringIsolationApiTests(PayrollApiFixture fixture) => _fixture = fixture;

    private static object Body(decimal amount = 100_000m, decimal? cap = 150_000m) =>
        new { conceptId = OtherIncome, mode = "Fixed", amount, salaryConstitutive = false, cap };

    private static string Url(string tenantId, Guid companyId, Guid employeeId) =>
        $"/api/v1/tenants/{tenantId}/payroll/companies/{companyId}/employees/{employeeId}/recurring-concepts";

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string url, bool body)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body)
        {
            request.Content = JsonContent.Create(Body());
        }

        if (method == HttpMethod.Put)
        {
            request.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        }

        return await client.SendAsync(request, Ct);
    }

    private static async Task<Guid> CreateRecurringAsync(PayrollTenant tenant, Guid companyId, Guid employeeId, decimal amount = 100_000m, decimal? cap = 150_000m)
    {
        var response = await tenant.Payroll.PostAsJsonAsync(Url(tenant.IdText, companyId, employeeId), Body(amount, cap), Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var list = await JsonOfAsync(response);
        return Assert.Single(list.EnumerateArray()).GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task EveryRecurringRouteRefusesAnotherTenantAndAnotherTenantsIds()
    {
        var tenantA = await NewTenantAsync(_fixture);
        var setupA = await SeedSetupAsync(tenantA);
        var employeeA = EmployeeIdOf(await HireAsync(tenantA, setupA));
        var tenantB = await NewTenantAsync(_fixture);
        var setupB = await SeedSetupAsync(tenantB);
        var employeeB = EmployeeIdOf(await HireAsync(tenantB, setupB));
        var recurringB = await CreateRecurringAsync(tenantB, setupB.Company.Id, employeeB);

        var routes = new (HttpMethod Method, string Suffix, bool Body)[]
        {
            (HttpMethod.Get, "", false),
            (HttpMethod.Post, "", true),
            (HttpMethod.Put, $"/{recurringB}", true),
            (HttpMethod.Post, $"/{recurringB}/activate", false),
            (HttpMethod.Post, $"/{recurringB}/deactivate", false),
        };

        foreach (var (method, suffix, body) in routes)
        {
            // B's route with A's session: 403 authorization.denied, nothing from B.
            var denied = await SendAsync(tenantA.Payroll, method, Url(tenantB.IdText, setupB.Company.Id, employeeB) + suffix, body);
            Assert.True(denied.StatusCode == HttpStatusCode.Forbidden, $"{method} {suffix}: {(int)denied.StatusCode}");
            Assert.Equal("authorization.denied", CodeOf(await JsonOfAsync(denied)));

            // A's route with B's company: the same 404 as an invented company.
            var company = await SendAsync(tenantA.Payroll, method, Url(tenantA.IdText, setupB.Company.Id, employeeA) + suffix, body);
            Assert.True(company.StatusCode == HttpStatusCode.NotFound, $"{method} {suffix}: {(int)company.StatusCode}");
            Assert.Equal("payroll.company.not_found", CodeOf(await JsonOfAsync(company)));

            // A's route and company with B's employee: no contract of A's company.
            var employee = await SendAsync(tenantA.Payroll, method, Url(tenantA.IdText, setupA.Company.Id, employeeB) + suffix, body);
            Assert.True(employee.StatusCode == HttpStatusCode.NotFound, $"{method} {suffix}: {(int)employee.StatusCode}");
            Assert.Equal("payroll.employee.not_found", CodeOf(await JsonOfAsync(employee)));
        }

        // A's own company and employee with B's recurring id: it does not exist for A.
        foreach (var (method, suffix) in new[]
                 {
                     (HttpMethod.Put, $"/{recurringB}"),
                     (HttpMethod.Post, $"/{recurringB}/activate"),
                     (HttpMethod.Post, $"/{recurringB}/deactivate"),
                 })
        {
            var missing = await SendAsync(
                tenantA.Payroll, method, Url(tenantA.IdText, setupA.Company.Id, employeeA) + suffix, method == HttpMethod.Put);
            Assert.True(missing.StatusCode == HttpStatusCode.NotFound, $"{method} {suffix}: {(int)missing.StatusCode}");
            Assert.Equal("payroll.recurring.not_found", CodeOf(await JsonOfAsync(missing)));
        }

        // B's recurring is untouched.
        var list = await JsonOfAsync(await tenantB.Payroll.GetAsync(Url(tenantB.IdText, setupB.Company.Id, employeeB), Ct));
        Assert.Equal("Active", Assert.Single(list.EnumerateArray()).GetProperty("status").GetString());
        tenantA.Dispose();
        tenantB.Dispose();
    }

    [Fact]
    public async Task ARecurringOfAnotherCompanyOfTheSameTenantIsNotReachableFromThisOne()
    {
        var tenant = await NewTenantAsync(_fixture);
        var first = await SeedSetupAsync(tenant);
        var second = await SeedSecondCompanyAsync(tenant);
        var employee = EmployeeIdOf(await HireAsync(tenant, first));
        var recurring = await CreateRecurringAsync(tenant, first.Company.Id, employee);

        // The employee has no contract with the second company, so its route answers 404 for every verb.
        var get = await tenant.Payroll.GetAsync(Url(tenant.IdText, second.Company.Id, employee), Ct);
        var put = await SendAsync(
            tenant.Payroll, HttpMethod.Put, Url(tenant.IdText, second.Company.Id, employee) + $"/{recurring}", true);
        var activate = await tenant.Payroll.PostAsync(
            Url(tenant.IdText, second.Company.Id, employee) + $"/{recurring}/activate", null, Ct);

        foreach (var response in new[] { get, put, activate })
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("payroll.employee.not_found", CodeOf(await JsonOfAsync(response)));
        }

        tenant.Dispose();
    }

    private static async Task<JsonElement> StatusOfAsync(PayrollTenant tenant, SeededSetup setup, Guid employeeId, Guid recurringId)
    {
        var list = await JsonOfAsync(await tenant.Payroll.GetAsync(Url(tenant.IdText, setup.Company.Id, employeeId), Ct));
        return list.EnumerateArray().Single(item => item.GetProperty("id").GetGuid() == recurringId);
    }

    [Fact]
    public async Task ACapBelongsToItsEmployee()
    {
        var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var ana = EmployeeIdOf(await HireAsync(tenant, HireBody(setup, documentNumber: "1001", firstName: "Ana", lastName: "Pérez")));
        var beto = EmployeeIdOf(await HireAsync(tenant, HireBody(setup, documentNumber: "1002", firstName: "Beto", lastName: "Ramírez")));
        var anaRecurring = await CreateRecurringAsync(tenant, setup.Company.Id, ana, 100_000m, 150_000m);
        var betoRecurring = await CreateRecurringAsync(tenant, setup.Company.Id, beto, 100_000m, 1_000_000m);
        var periodsUrl = $"{tenant.Url}/companies/{setup.Company.Id}/periods/ensure-current";
        var firstId = (await JsonOfAsync(await tenant.Payroll.PostAsync(periodsUrl, null, Ct))).GetProperty("items")[0].GetProperty("id").GetGuid();

        async Task LiquidateAsync(Guid periodId)
        {
            var url = $"{tenant.Url}/periods/{periodId}";
            var version = (await JsonOfAsync(await tenant.Payroll.GetAsync(url, Ct))).GetProperty("period").GetProperty("version").GetInt64();
            var response = await PostAsync(tenant.Payroll, $"{url}/liquidate", $"\"{version}\"", new { confirmNoAbsences = true });
            Assert.True(response.StatusCode == HttpStatusCode.OK, await BodyAsync(response));
        }

        async Task<decimal> AppliedAsync(Guid periodId, Guid employeeId, Guid recurringId)
        {
            var employee = await JsonOfAsync(await tenant.Payroll.GetAsync($"{tenant.Url}/periods/{periodId}/employees/{employeeId}", Ct));
            return employee.GetProperty("result").GetProperty("lines").EnumerateArray()
                .Single(line => line.GetProperty("code").GetString() == $"recurring:{recurringId}").GetProperty("amount").GetDecimal();
        }

        await LiquidateAsync(firstId);
        _fixture.Clock.UtcNow = new DateTimeOffset(2026, 11, 25, 15, 0, 0, TimeSpan.Zero);
        var list = await JsonOfAsync(await tenant.Payroll.PostAsync(periodsUrl, null, Ct));
        var secondId = list.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("half").GetString() == "Second").GetProperty("id").GetGuid();
        await LiquidateAsync(secondId);

        // Ana's cap is spent (100 000 + 50 000); Beto's recurring keeps paying the whole 100 000 in the second half.
        Assert.Equal(100_000m, await AppliedAsync(firstId, ana, anaRecurring));
        Assert.Equal(50_000m, await AppliedAsync(secondId, ana, anaRecurring));
        Assert.Equal(100_000m, await AppliedAsync(firstId, beto, betoRecurring));
        Assert.Equal(100_000m, await AppliedAsync(secondId, beto, betoRecurring));

        var anaState = await StatusOfAsync(tenant, setup, ana, anaRecurring);
        Assert.Equal("Exhausted", anaState.GetProperty("status").GetString());
        Assert.Equal(150_000m, anaState.GetProperty("accumulated").GetDecimal());
        var betoState = await StatusOfAsync(tenant, setup, beto, betoRecurring);
        Assert.Equal("Active", betoState.GetProperty("status").GetString());
        Assert.Equal(200_000m, betoState.GetProperty("accumulated").GetDecimal());
        Assert.Equal(800_000m, betoState.GetProperty("remaining").GetDecimal());
        tenant.Dispose();
    }
}
