using System.Net;
using System.Net.Http.Json;
using Modules.Payroll.Application;
using Modules.Tenancy.Domain;
using static Modules.Payroll.IntegrationTests.PayrollApiHarness;
using static Modules.Payroll.IntegrationTests.PayrollApiSeeds;

namespace Modules.Payroll.IntegrationTests;

/// <summary>
/// La matriz de permisos: una fila por ruta del contrato, con lo que exige. Sin <b>cualquiera</b> de los permisos de la fila es 403;
/// con la lista completa es distinto de 403 (la respuesta real, 200, 404, 412 o 422, la prueban las clases de cada ruta). Las filas
/// «A o B» (<c>SettingsView</c>) aceptan cada permiso por separado. Las pruebas comparten un solo escenario: ninguna fila muta nada
/// (las escrituras van con un <c>If-Match</c> que nunca existe).
/// </summary>
public sealed class PayrollAccessApiTests : IClassFixture<PayrollApiFixture>
{
    private const string Read = PayrollPermissions.PeriodRead;
    private const string Manage = PayrollPermissions.PeriodManage;
    private const string Approve = PayrollPermissions.PeriodApprove;
    private const string Settings = PayrollPermissions.SettingsManage;

    private static readonly Guid CommissionsId = Guid.Parse("00000000-0000-0000-0002-000000000001");

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Scene? _scene;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly PayrollApiFixture _fixture;

    public PayrollAccessApiTests(PayrollApiFixture fixture) => _fixture = fixture;

    /// <param name="Name">El nombre de la fila.</param>
    /// <param name="Required">Todos estos permisos hacen falta (política más handler).</param>
    /// <param name="AnyOf">Basta uno; vacío cuando la fila usa <paramref name="Required"/>.</param>
    internal sealed record Route(string Name, HttpMethod Method, string Path, string[] Required, string[] AnyOf, bool Multipart = false)
    {
        public bool IsSatisfiedBy(IReadOnlyCollection<string> permissions) =>
            AnyOf.Length > 0 ? AnyOf.Any(permissions.Contains) : Required.All(permissions.Contains);
    }

    private static readonly Route[] Routes =
    [
        new("companies", HttpMethod.Get, "/companies", [], [Read, Settings]),
        new("settings-get", HttpMethod.Get, "/companies/{c}/settings", [], [Read, Settings]),
        new("settings-put", HttpMethod.Put, "/companies/{c}/settings", [Settings], []),
        new("concept-create", HttpMethod.Post, "/companies/{c}/concepts", [Settings], []),
        new("concept-update", HttpMethod.Put, "/companies/{c}/concepts/{k}", [Settings], []),
        new("concept-archive", HttpMethod.Post, "/companies/{c}/concepts/{k}/archive", [Settings], []),
        new("concept-unarchive", HttpMethod.Post, "/companies/{c}/concepts/{k}/unarchive", [Settings], []),
        new("recurring-list", HttpMethod.Get, "/companies/{c}/employees/{e}/recurring-concepts", [], [Read, Settings]),
        new("recurring-create", HttpMethod.Post, "/companies/{c}/employees/{e}/recurring-concepts", [Settings], []),
        new("recurring-update", HttpMethod.Put, "/companies/{c}/employees/{e}/recurring-concepts/{r}", [Settings], []),
        new("recurring-activate", HttpMethod.Post, "/companies/{c}/employees/{e}/recurring-concepts/{r}/activate", [Settings], []),
        new("recurring-deactivate", HttpMethod.Post, "/companies/{c}/employees/{e}/recurring-concepts/{r}/deactivate", [Settings], []),
        new("vacations-list", HttpMethod.Get, "/companies/{c}/employees/{e}/vacations", [Read], []),
        new("vacations-preview", HttpMethod.Post, "/companies/{c}/employees/{e}/vacations/preview", [Read, Manage], []),
        new("vacations-create", HttpMethod.Post, "/companies/{c}/employees/{e}/vacations", [Read, Manage], []),
        new("vacations-update", HttpMethod.Put, "/companies/{c}/employees/{e}/vacations/{r}", [Read, Manage], []),
        new("vacations-delete", HttpMethod.Delete, "/companies/{c}/employees/{e}/vacations/{r}", [Read, Manage], []),
        new("ensure-current", HttpMethod.Post, "/companies/{c}/periods/ensure-current", [Read, Manage], []),
        new("open-next", HttpMethod.Post, "/companies/{c}/periods/open-next", [Read, Manage], []),
        new("periods-list", HttpMethod.Get, "/companies/{c}/periods", [Read], []),
        new("period-detail", HttpMethod.Get, "/periods/{p}", [Read], []),
        new("period-employee", HttpMethod.Get, "/periods/{p}/employees/{e}", [Read], []),
        new("period-export", HttpMethod.Get, "/periods/{p}/export", [Read], []),
        new("inputs-put", HttpMethod.Put, "/periods/{p}/employees/{e}/inputs", [Read, Manage], []),
        new("inputs-template", HttpMethod.Get, "/periods/{p}/inputs/template", [Read, Manage], []),
        new("import-preview", HttpMethod.Post, "/periods/{p}/inputs/import/preview", [Read, Manage], [], Multipart: true),
        new("import", HttpMethod.Post, "/periods/{p}/inputs/import", [Read, Manage], [], Multipart: true),
        new("liquidate", HttpMethod.Post, "/periods/{p}/liquidate", [Read, Manage], []),
        new("approve", HttpMethod.Post, "/periods/{p}/approve", [Read, Approve], []),
        new("close", HttpMethod.Post, "/periods/{p}/close", [Read, Manage], []),
        new("reopen", HttpMethod.Post, "/periods/{p}/reopen", [Read, Manage], []),
    ];

    public static TheoryData<string> RouteNames() => new(Routes.Select(route => route.Name));

    private sealed record Scene(PayrollTenant Tenant, Guid CompanyId, Guid PeriodId, Guid EmployeeId);

    private async Task<Scene> SceneAsync()
    {
        await Gate.WaitAsync(Ct);
        try
        {
            if (_scene is not null)
            {
                return _scene;
            }

            var tenant = await NewTenantAsync(_fixture);
            var setup = await SeedSetupAsync(tenant);
            var employee = await HireAsync(tenant, setup);
            var ensured = await tenant.Payroll.PostAsync($"{tenant.Url}/companies/{setup.Company.Id}/periods/ensure-current", null, Ct);
            Assert.Equal(HttpStatusCode.OK, ensured.StatusCode);
            var periodId = (await JsonOfAsync(ensured)).GetProperty("items")[0].GetProperty("id").GetGuid();
            return _scene = new Scene(tenant, setup.Company.Id, periodId, EmployeeIdOf(employee));
        }
        finally
        {
            Gate.Release();
        }
    }

    private static readonly HashSet<string> WithoutBody =
    [
        "concept-archive", "concept-unarchive", "recurring-activate", "recurring-deactivate", "ensure-current", "open-next", "approve", "close",
    ];

    private static object BodyOf(Route route) => route.Name switch
    {
        "settings-put" => new { transportFrequency = "Biweekly", transportCapBase = "SalaryOnly", overtimeBase = "SalaryOnly", exonerated = true, dualControl = false },
        "concept-create" or "concept-update" => new { kind = "Income", name = "Bono de matriz", defaultSalaryConstitutive = (bool?)false, multiplier = (decimal?)null },
        "recurring-create" or "recurring-update" => new { conceptId = CommissionsId, mode = "Fixed", amount = 100_000m, salaryConstitutive = (bool?)false, cap = (decimal?)null },
        "inputs-put" => new { overtime = Array.Empty<object>(), incomes = Array.Empty<object>(), deductions = Array.Empty<object>() },
        "liquidate" => new { confirmNoAbsences = true },
        "reopen" => new { reason = "Corregir" },
        _ => new { },
    };

    private static HttpRequestMessage RequestOf(Route route, string tenantId, Guid companyId, Guid periodId, Guid employeeId)
    {
        var path = route.Path
            .Replace("{c}", companyId.ToString())
            .Replace("{p}", periodId.ToString())
            .Replace("{e}", employeeId.ToString())
            .Replace("{k}", CommissionsId.ToString())
            .Replace("{r}", Guid.NewGuid().ToString());
        var request = new HttpRequestMessage(route.Method, $"/api/v1/tenants/{tenantId}/payroll{path}");
        if (route.Multipart)
        {
            var form = new MultipartFormDataContent();
            var file = new ByteArrayContent([1, 2, 3]);
            file.Headers.ContentType = new("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
            form.Add(file, "file", "novedades.xlsx");
            request.Content = form;
        }
        else if (route.Method != HttpMethod.Get && !WithoutBody.Contains(route.Name))
        {
            request.Content = JsonContent.Create(BodyOf(route));
        }

        // A version that never exists: whatever the handler does after authorizing, it cannot change the shared scene.
        request.Headers.TryAddWithoutValidation("If-Match", "\"9999\"");
        return request;
    }

    private static async Task<HttpStatusCode> SendAsync(Route route, HttpClient client, string tenantId, Guid companyId, Guid periodId, Guid employeeId)
    {
        using var request = RequestOf(route, tenantId, companyId, periodId, employeeId);
        using var response = await client.SendAsync(request, Ct);
        return response.StatusCode;
    }

    private static Task<HttpStatusCode> SendAsync(Route route, HttpClient client, Scene scene) =>
        SendAsync(route, client, scene.Tenant.IdText, scene.CompanyId, scene.PeriodId, scene.EmployeeId);

    private static Route RouteNamed(string name) => Routes.Single(route => route.Name == name);

    [Theory]
    [MemberData(nameof(RouteNames))]
    public async Task EachRouteRequiresItsPermissions(string name)
    {
        var scene = await SceneAsync();
        var route = RouteNamed(name);

        if (route.AnyOf.Length > 0)
        {
            foreach (var permission in route.AnyOf)
            {
                using var only = scene.Tenant.Client(permission);
                var status = await SendAsync(route, only, scene);
                Assert.True(status != HttpStatusCode.Forbidden, $"{name} with only {permission}: {(int)status}");
            }

            foreach (var permission in new[] { Read, Manage, Approve, Settings }.Except(route.AnyOf))
            {
                using var stranger = scene.Tenant.Client(permission);
                Assert.Equal(HttpStatusCode.Forbidden, await SendAsync(route, stranger, scene));
            }

            return;
        }

        using var full = scene.Tenant.Client(route.Required);
        var fullStatus = await SendAsync(route, full, scene);
        Assert.True(fullStatus != HttpStatusCode.Forbidden, $"{name} with the full list: {(int)fullStatus}");
        foreach (var missing in route.Required)
        {
            using var partial = scene.Tenant.Client(route.Required.Where(permission => permission != missing).ToArray());
            Assert.Equal(HttpStatusCode.Forbidden, await SendAsync(route, partial, scene));
        }
    }

    [Theory]
    [MemberData(nameof(RouteNames))]
    public async Task ASettingsOnlyRoleSeesOnlyTheSettingsScreens(string name)
    {
        var scene = await SceneAsync();
        var route = RouteNamed(name);
        using var settingsOnly = scene.Tenant.Client(Settings);

        var status = await SendAsync(route, settingsOnly, scene);

        if (route.IsSatisfiedBy([Settings]))
        {
            Assert.True(status != HttpStatusCode.Forbidden, $"{name}: {(int)status}");
        }
        else
        {
            Assert.Equal(HttpStatusCode.Forbidden, status);
        }
    }

    [Theory]
    [MemberData(nameof(RouteNames))]
    public async Task AReadOnlyRoleCannotWriteSettings(string name)
    {
        var scene = await SceneAsync();
        var route = RouteNamed(name);
        using var readOnly = scene.Tenant.Client(Read);

        var status = await SendAsync(route, readOnly, scene);

        if (route.IsSatisfiedBy([Read]))
        {
            Assert.True(status != HttpStatusCode.Forbidden, $"{name}: {(int)status}");
        }
        else
        {
            Assert.Equal(HttpStatusCode.Forbidden, status);
        }
    }

    [Theory]
    [MemberData(nameof(RouteNames))]
    public async Task WithThePayrollModuleOffEverythingIs403(string name)
    {
        using var tenant = await NewTenantAsync(_fixture, TenantModuleKeys.All.Where(key => key != TenantModuleKeys.Payroll));

        Assert.Equal(
            HttpStatusCode.Forbidden,
            await SendAsync(RouteNamed(name), tenant.Payroll, tenant.IdText, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));
    }

    [Theory]
    [MemberData(nameof(RouteNames))]
    public async Task WithHrOffPayrollIsOffToo(string name)
    {
        using var tenant = await NewTenantAsync(_fixture, TenantModuleKeys.All.Where(key => key != TenantModuleKeys.Hr));

        Assert.Equal(
            HttpStatusCode.Forbidden,
            await SendAsync(RouteNamed(name), tenant.Payroll, tenant.IdText, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));
    }

    [Fact]
    public async Task ACompanyOfAnotherTenantIs404()
    {
        var scene = await SceneAsync();
        using var other = await NewTenantAsync(_fixture);
        var foreign = await CreateCompanyAsync(other.Admin, other.Id, "Empresa Ajena S.A.S.");

        foreach (var name in new[] { "settings-get", "settings-put", "concept-create", "recurring-list", "ensure-current", "open-next", "periods-list" })
        {
            using var request = RequestOf(RouteNamed(name), scene.Tenant.IdText, foreign.Id, scene.PeriodId, scene.EmployeeId);
            using var response = await scene.Tenant.Payroll.SendAsync(request, Ct);
            var body = await BodyAsync(response);
            Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{name}: {(int)response.StatusCode} {body}");
            Assert.DoesNotContain("Empresa Ajena", body, StringComparison.Ordinal);
        }
    }
}
