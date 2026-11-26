using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Hr.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;
using Testcontainers.PostgreSql;
using static Modules.Hr.IntegrationTests.HrApiHarness;

namespace Modules.Hr.IntegrationTests;

/// <summary>
/// Una base para toda una clase de pruebas, con el reloj del tenant movible y el PDF en memoria. Cada prueba trabaja en su propio
/// tenant (<see cref="HrApiSeeds.NewTenantAsync"/>), así que no se estorban. El reloj arranca en 2026-11-20 10:00 en Bogotá.
/// </summary>
public sealed class HrApiFixture : IAsyncLifetime
{
    public static readonly DateTimeOffset DefaultNow = new(2026, 11, 20, 15, 0, 0, TimeSpan.Zero);

    private PostgreSqlContainer? _database;

    internal MutableTenantClock Clock { get; } = new(DefaultNow);

    internal FakeHrDocumentStore Documents { get; } = new();

    internal QepApiFactory Factory { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        _database = await StartDatabaseAsync();
        var clock = Clock;
        var documents = Documents;
        Factory = new QepApiFactory(_database.GetConnectionString(), services =>
        {
            services.RemoveAll<ITenantClock>();
            services.AddSingleton<ITenantClock>(clock);
            services.RemoveAll<IHrDocumentStore>();
            services.AddSingleton<IHrDocumentStore>(documents);
        });
    }

    public async ValueTask DisposeAsync()
    {
        Factory?.Dispose();
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }
}

/// <summary>Un tenant de prueba con sus clientes: <see cref="Hr"/> puede todo en el módulo y <see cref="Admin"/> sólo gestiona empresas.</summary>
internal sealed class HrTenant(HrApiFixture fixture, string idText, HttpClient hr, HttpClient admin) : IDisposable
{
    public HrApiFixture Fixture { get; } = fixture;

    public string IdText { get; } = idText;

    public Guid Id { get; } = Guid.Parse(idText);

    public HttpClient Hr { get; } = hr;

    public HttpClient Admin { get; } = admin;

    public string Url => HrUrl(IdText);

    /// <summary>Un cliente del mismo tenant con sólo los permisos pedidos.</summary>
    public HttpClient Client(params string[] permissions) => CreateClient(Fixture.Factory, SubjectId, IdText, permissions);

    public void Dispose()
    {
        Hr.Dispose();
        Admin.Dispose();
    }
}

internal sealed record SeededCompany(CompanyRef Company, Guid SiteId, Guid WorkplaceId);

/// <summary>La configuración mínima para dar de alta: empresa, sede «Principal», centro «Bodega», área «Operaciones» y cargo «Analista».</summary>
internal sealed record SeededSetup(CompanyRef Company, Guid SiteId, Guid WorkplaceId, Guid AreaId, Guid PositionId)
{
    public SeededCompany AsCompany() => new(Company, SiteId, WorkplaceId);
}

internal static class HrApiSeeds
{
    // Los dos últimos dígitos del id deben ser distintos: el slug del tenant sale de ellos.
    private static int _tenantCounter = 0x10;

    public static readonly string[] AllPermissions =
    [
        HrPermissions.EmployeeRead, HrPermissions.EmployeeManage, HrPermissions.ContractRead, HrPermissions.ContractManage,
        HrPermissions.SettingsManage, HrPermissions.MedicalRead, HrPermissions.MedicalManage,
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Un tenant nuevo, con el reloj en <see cref="HrApiFixture.DefaultNow"/>. <paramref name="country"/> es el del tenant.</summary>
    public static async Task<HrTenant> NewTenantAsync(
        HrApiFixture fixture, string country = "CO", IEnumerable<TenantModuleKey>? modules = null)
    {
        fixture.Clock.UtcNow = HrApiFixture.DefaultNow;
        var idText = $"01900000-0000-7000-8000-0000000000{Interlocked.Increment(ref _tenantCounter):x2}";
        await SeedTenantAsync(fixture.Factory, idText, modules, country);
        var hr = CreateClient(fixture.Factory, SubjectId, idText, AllPermissions);
        var admin = CreateClient(fixture.Factory, SubjectId, idText, "companies.company.read", "companies.company.manage");
        return new HrTenant(fixture, idText, hr, admin);
    }

    public static async Task<JsonElement> JsonOfAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await BodyAsync(response));
        return document.RootElement.Clone();
    }

    public static async Task<JsonElement> CreatedAsync(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await BodyAsync(response));
        return await JsonOfAsync(response);
    }

    public static async Task<HttpResponseMessage> PutAsync(HttpClient client, string url, object body, string? ifMatch)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = JsonContent.Create(body) };
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return await client.SendAsync(request, Ct);
    }

    public static string CodeOf(JsonElement problem) => problem.GetProperty("code").GetString()!;

    public static bool HasError(JsonElement problem, string key) =>
        problem.TryGetProperty("errors", out var errors) && errors.TryGetProperty(key, out _);

    public static string Version(JsonElement detail) => $"\"{detail.GetProperty("employee").GetProperty("version").GetInt64()}\"";

    /// <summary>Una empresa con su sede y su centro: lo que el alta necesita para un lugar de trabajo.</summary>
    public static async Task<SeededCompany> SeedCompanyAsync(
        HrTenant tenant, string companyName, string siteName, string workplaceName)
    {
        var company = await CreateCompanyAsync(tenant.Admin, tenant.Id, companyName);
        var site = await CreatedAsync(
            tenant.Hr, $"{tenant.Url}/sites", new { companyId = company.Id, name = siteName, address = (string?)null, cityId = (Guid?)null, compensationFund = (string?)null });
        var siteId = site.GetProperty("id").GetGuid();
        var workplace = await CreatedAsync(
            tenant.Hr, $"{tenant.Url}/workplaces", new { name = workplaceName, siteId, ciiuCode = "4711", riskClass = "III", highRiskPension = false });
        return new SeededCompany(company, siteId, workplace.GetProperty("id").GetGuid());
    }

    /// <summary>Empresa «Comercializadora QEP S.A.S.», sede «Principal», centro «Bodega», área «Operaciones» y cargo «Analista».</summary>
    public static async Task<SeededSetup> SeedSetupAsync(HrTenant tenant)
    {
        var first = await SeedCompanyAsync(tenant, "Comercializadora QEP S.A.S.", "Principal", "Bodega");
        var area = await CreatedAsync(tenant.Hr, $"{tenant.Url}/areas", new { name = "Operaciones" });
        var position = await CreatedAsync(tenant.Hr, $"{tenant.Url}/positions", new { name = "Analista" });
        return new SeededSetup(first.Company, first.SiteId, first.WorkplaceId, area.GetProperty("id").GetGuid(), position.GetProperty("id").GetGuid());
    }

    /// <summary>Otra empresa con su sede «Norte» y su centro «Planta».</summary>
    public static Task<SeededCompany> SeedSecondCompanyAsync(HrTenant tenant) =>
        SeedCompanyAsync(tenant, "Servicios del Norte S.A.S.", "Norte", "Planta");

    /// <summary>
    /// El cuerpo de un alta (<c>POST /hr/employees</c>): esa empresa, <c>Employee</c>, <c>FixedTerm</c>, <c>Monthly</c>, del
    /// 2026-01-15 al 2027-01-14, 2 500 000, documento <c>CC</c> y país nulo. Un diccionario: cada prueba cambia lo suyo.
    /// </summary>
    public static Dictionary<string, object?> HireBody(
        SeededSetup setup,
        Guid? companyId = null,
        string contractType = "Employee",
        string duration = "FixedTerm",
        string salaryType = "Monthly",
        string startDate = "2026-01-15",
        string? endDate = "2027-01-14",
        decimal amount = 2_500_000m,
        bool acknowledgeBelowMinimum = false,
        string documentType = "CC",
        string documentNumber = "1020304050",
        string? country = null,
        bool foreignPensionExempt = false,
        Guid? workplaceId = null,
        Guid? positionId = null,
        Guid? areaId = null,
        object? payment = null,
        Guid? memberId = null,
        Guid? cityId = null,
        string firstName = "Ana",
        string lastName = "Pérez",
        Guid? documentFileId = null) => new()
        {
            ["firstName"] = firstName,
            ["lastName"] = lastName,
            ["documentType"] = documentType,
            ["documentNumber"] = documentNumber,
            ["country"] = country,
            ["foreignPensionExempt"] = foreignPensionExempt,
            ["cityId"] = cityId,
            ["memberId"] = memberId,
            ["payment"] = payment,
            ["contract"] = new Dictionary<string, object?>
            {
                ["contract"] = new Dictionary<string, object?>
                {
                    ["companyId"] = companyId ?? setup.Company.Id,
                    ["contractType"] = contractType,
                    ["duration"] = duration,
                    ["salaryType"] = salaryType,
                    ["startDate"] = startDate,
                    ["endDate"] = endDate,
                    ["documentFileId"] = documentFileId,
                },
                ["salary"] = new Dictionary<string, object?>
                {
                    ["amount"] = amount,
                    ["teleworker"] = false,
                    ["acknowledgeBelowMinimum"] = acknowledgeBelowMinimum,
                },
                ["assignment"] = new Dictionary<string, object?>
                {
                    ["positionId"] = positionId ?? setup.PositionId,
                    ["areaId"] = areaId ?? setup.AreaId,
                    ["workplaceId"] = workplaceId ?? setup.WorkplaceId,
                },
            },
        };

    /// <summary>Un <c>NewContractRequest</c> suelto (reincorporación y conversión): el mismo contrato que <see cref="HireBody"/>.</summary>
    public static Dictionary<string, object?> NewContractBody(
        SeededSetup setup,
        string startDate,
        Guid? companyId = null,
        string contractType = "Employee",
        string duration = "Indefinite",
        string salaryType = "Monthly",
        string? endDate = null,
        decimal amount = 2_500_000m,
        Guid? workplaceId = null,
        Guid? positionId = null) =>
        (Dictionary<string, object?>)HireBody(
            setup, companyId, contractType, duration, salaryType, startDate, endDate, amount, workplaceId: workplaceId, positionId: positionId)["contract"]!;

    /// <summary>El cuerpo de <c>PUT /hr/employees/{id}</c> a partir del alta: el <c>PUT</c> reemplaza todo y no lleva contrato ni pago.</summary>
    public static Dictionary<string, object?> UpdateBody(Dictionary<string, object?> hire)
    {
        var body = new Dictionary<string, object?>(hire);
        body.Remove("contract");
        body.Remove("payment");
        return body;
    }

    public static async Task<HttpResponseMessage> PostEmployeeAsync(HrTenant tenant, Dictionary<string, object?> body) =>
        await tenant.Hr.PostAsJsonAsync($"{tenant.Url}/employees", body, Ct);

    /// <summary>Da de alta y espera 201: devuelve el <c>EmployeeDetailDto</c>.</summary>
    public static async Task<JsonElement> HireAsync(HrTenant tenant, Dictionary<string, object?> body)
    {
        var response = await PostEmployeeAsync(tenant, body);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await BodyAsync(response));
        return await JsonOfAsync(response);
    }

    public static Task<JsonElement> HireAsync(HrTenant tenant, SeededSetup setup, string documentNumber = "1020304050") =>
        HireAsync(tenant, HireBody(setup, documentNumber: documentNumber));

    public static string EmployeeUrl(HrTenant tenant, JsonElement detail) =>
        $"{tenant.Url}/employees/{detail.GetProperty("employee").GetProperty("id").GetGuid()}";

    public static async Task<JsonElement> GetEmployeeAsync(HrTenant tenant, JsonElement detail, HttpClient? client = null)
    {
        var response = await (client ?? tenant.Hr).GetAsync(EmployeeUrl(tenant, detail), Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await JsonOfAsync(response);
    }
}
