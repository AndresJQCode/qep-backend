using System.Net;
using System.Net.Http.Json;
using static Modules.Payroll.IntegrationTests.PayrollApiHarness;
using static Modules.Payroll.IntegrationTests.PayrollApiSeeds;
using static Modules.Payroll.IntegrationTests.PayrollExportApiTests;

namespace Modules.Payroll.IntegrationTests;

/// <summary>
/// Un tenant nunca ve nómina de otro (Review Focus 6). Con la sesión del tenant A, la ruta del tenant B es 403
/// <c>authorization.denied</c> en cada ruta de un período; y la ruta de A con el id de un período de B es 404
/// <c>payroll.period.not_found</c>, el mismo que un id inventado, sin ningún dato de B.
/// </summary>
public sealed class PayrollIsolationApiTests : IClassFixture<PayrollApiFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly PayrollApiFixture _fixture;

    public PayrollIsolationApiTests(PayrollApiFixture fixture) => _fixture = fixture;

    private sealed record RouteCase(string Name, HttpMethod Method, string Path, bool NeedsIfMatch, Func<HttpContent>? Body);

    private static MultipartFormDataContent Upload()
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent([1, 2, 3]);
        file.Headers.ContentType = new("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        form.Add(file, "file", "novedades.xlsx");
        return form;
    }

    // Every `{p}…` route of the contract, with a body that is valid in shape: the period lookup must be what answers.
    private static List<RouteCase> Routes(Guid employeeId) =>
    [
        new("detail", HttpMethod.Get, "", false, null),
        new("employee", HttpMethod.Get, $"/employees/{employeeId}", false, null),
        new(
            "inputs",
            HttpMethod.Put,
            $"/employees/{employeeId}/inputs",
            true,
            () => JsonContent.Create(new { overtime = Array.Empty<object>(), incomes = Array.Empty<object>(), deductions = Array.Empty<object>() })),
        new("liquidate", HttpMethod.Post, "/liquidate", true, () => JsonContent.Create(new { confirmNoAbsences = true })),
        new("approve", HttpMethod.Post, "/approve", true, null),
        new("close", HttpMethod.Post, "/close", true, null),
        new("reopen", HttpMethod.Post, "/reopen", true, () => JsonContent.Create(new { reason = "Corregir" })),
        new("export", HttpMethod.Get, "/export", false, null),
        new("template", HttpMethod.Get, "/inputs/template", false, null),
        new("preview", HttpMethod.Post, "/inputs/import/preview", false, Upload),
        new("import", HttpMethod.Post, "/inputs/import", true, Upload),
    ];

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, RouteCase route, string tenantId, Guid periodId)
    {
        using var request = new HttpRequestMessage(route.Method, $"/api/v1/tenants/{tenantId}/payroll/periods/{periodId}{route.Path}");
        if (route.Body is not null)
        {
            request.Content = route.Body();
        }

        if (route.NeedsIfMatch)
        {
            request.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        }

        return await client.SendAsync(request, Ct);
    }

    [Fact]
    public async Task AnotherTenantsPeriodIsForbiddenEverywhere()
    {
        var tenantA = await NewTenantAsync(_fixture);
        var sceneB = await NewSceneAsync(_fixture, companyName: "Servicios Reservados de B S.A.S.");
        await TransitionAsync(sceneB, "liquidate", new { confirmNoAbsences = true });

        var routes = Routes(sceneB.EmployeeId);
        foreach (var route in routes)
        {
            // The route of tenant B with the session of tenant A.
            var denied = await SendAsync(tenantA.Payroll, route, sceneB.Tenant.IdText, sceneB.PeriodId);
            Assert.True(denied.StatusCode == HttpStatusCode.Forbidden, $"{route.Name}: {(int)denied.StatusCode}");
            var deniedBody = await BodyAsync(denied);
            Assert.Equal("authorization.denied", CodeOf(System.Text.Json.JsonDocument.Parse(deniedBody).RootElement));
            Assert.DoesNotContain("Servicios Reservados de B", deniedBody, StringComparison.Ordinal);
            Assert.DoesNotContain("1020304050", deniedBody, StringComparison.Ordinal);

            // The route of tenant A with the period id of tenant B: the same 404 as an invented id.
            var missing = await SendAsync(tenantA.Payroll, route, tenantA.IdText, sceneB.PeriodId);
            Assert.True(missing.StatusCode == HttpStatusCode.NotFound, $"{route.Name}: {(int)missing.StatusCode}");
            var missingBody = await BodyAsync(missing);
            Assert.Equal("payroll.period.not_found", CodeOf(System.Text.Json.JsonDocument.Parse(missingBody).RootElement));
            Assert.DoesNotContain("Servicios Reservados de B", missingBody, StringComparison.Ordinal);
            Assert.DoesNotContain("1020304050", missingBody, StringComparison.Ordinal);
        }

        // B keeps its data untouched after all the attempts.
        var detail = await sceneB.Tenant.Payroll.GetAsync(sceneB.PeriodUrl, Ct);
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        Assert.Contains("\"status\":\"Liquidated\"", await BodyAsync(detail), StringComparison.Ordinal);
        tenantA.Dispose();
        sceneB.Tenant.Dispose();
    }
}
