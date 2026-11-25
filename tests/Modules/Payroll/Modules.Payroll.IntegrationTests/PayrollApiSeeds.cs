using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Hr.Application;
using Modules.Payroll.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;
using Testcontainers.PostgreSql;
using static Modules.Payroll.IntegrationTests.PayrollApiHarness;

namespace Modules.Payroll.IntegrationTests;

/// <summary>
/// Una base para toda una clase de pruebas, con el reloj del tenant movible. Cada prueba trabaja en su propio tenant
/// (<see cref="PayrollApiSeeds.NewTenantAsync"/>), así que no se estorban. El reloj arranca en 2026-11-10 10:00 en Bogotá.
/// <see cref="StripAssignmentsOf"/> hace que el directorio de Hr responda sin cargos para un empleado (un bloqueado que por la API
/// de Hr no se puede producir).
/// </summary>
public class PayrollApiFixture : IAsyncLifetime
{
    public static readonly DateTimeOffset DefaultNow = new(2026, 11, 10, 15, 0, 0, TimeSpan.Zero);

    private PostgreSqlContainer? _database;

    internal MutableTenantClock Clock { get; } = new(DefaultNow);

    internal QepApiFactory Factory { get; private set; } = null!;

    /// <summary>El tenant que hace de operador de la plataforma en esta base; nulo = ninguno. Lo fija una subclase.</summary>
    protected virtual Guid? OperatorTenant => null;

    /// <summary>El empleado al que el directorio le quita los cargos; nulo = ninguno.</summary>
    internal Guid? StripAssignmentsOf { get; set; }

    public async ValueTask InitializeAsync()
    {
        _database = await StartDatabaseAsync();
        var clock = Clock;
        Factory = new QepApiFactory(_database.GetConnectionString(), services =>
        {
            services.RemoveAll<ITenantClock>();
            services.AddSingleton<ITenantClock>(clock);

            // Decora el adaptador real: la prueba de un bloqueado quita los cargos de un empleado sin tocar Hr.
            var original = services.Single(descriptor => descriptor.ServiceType == typeof(IPayrollHrDirectory));
            services.Remove(original);
            services.AddScoped<IPayrollHrDirectory>(provider => new StrippingHrDirectory(
                (IPayrollHrDirectory)ActivatorUtilities.CreateInstance(provider, original.ImplementationType!),
                () => StripAssignmentsOf));
        }, OperatorTenant);
    }

    public async ValueTask DisposeAsync()
    {
        Factory?.Dispose();
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }

    private sealed class StrippingHrDirectory(IPayrollHrDirectory inner, Func<Guid?> stripped) : IPayrollHrDirectory
    {
        public Task<IReadOnlyList<PayrollCompanyRef>> ListActiveCompaniesAsync(Guid tenantId, CancellationToken cancellationToken) =>
            inner.ListActiveCompaniesAsync(tenantId, cancellationToken);

        public Task<PayrollCompanyRef?> FindCompanyAsync(Guid tenantId, Guid companyId, CancellationToken cancellationToken) =>
            inner.FindCompanyAsync(tenantId, companyId, cancellationToken);

        public async Task<IReadOnlyList<HrEmployeeSource>> ListEmployeesAsync(
            Guid tenantId, Guid companyId, DateOnly periodStart, DateOnly periodEnd, CancellationToken cancellationToken)
        {
            var employees = await inner.ListEmployeesAsync(tenantId, companyId, periodStart, periodEnd, cancellationToken);
            return stripped() is not { } employeeId
                ? employees
                : employees
                    .Select(employee => employee.EmployeeId != employeeId
                        ? employee
                        : employee with
                        {
                            Contracts = employee.Contracts.Select(contract => contract with { Assignments = [] }).ToList(),
                        })
                    .ToList();
        }
    }
}

/// <summary>Un tenant de prueba con sus clientes: <see cref="Payroll"/> puede todo en el módulo y <see cref="Admin"/> sólo gestiona empresas.</summary>
internal sealed class PayrollTenant(PayrollApiFixture fixture, string idText, HttpClient payroll, HttpClient admin) : IDisposable
{
    public PayrollApiFixture Fixture { get; } = fixture;

    public string IdText { get; } = idText;

    public Guid Id { get; } = Guid.Parse(idText);

    /// <summary>Los cuatro permisos de nómina y los cinco de Hr.</summary>
    public HttpClient Payroll { get; } = payroll;

    public HttpClient Admin { get; } = admin;

    public string Url => PayrollUrl(IdText);

    public string HrBase => HrUrl(IdText);

    /// <summary>Un cliente del mismo tenant con sólo los permisos pedidos.</summary>
    public HttpClient Client(params string[] permissions) => CreateClient(Fixture.Factory, SubjectId, IdText, permissions);

    public void Dispose()
    {
        Payroll.Dispose();
        Admin.Dispose();
    }
}

/// <summary>Una empresa con su sede y su centro de trabajo, dados de alta por la API de Hr.</summary>
internal sealed record SeededCompany(CompanyRef Company, Guid SiteId, Guid WorkplaceId);

/// <summary>La configuración mínima de Hr para dar de alta: empresa, sede «Principal», centro «Bodega», área «Operaciones» y cargo «Analista».</summary>
internal sealed record SeededSetup(CompanyRef Company, Guid SiteId, Guid WorkplaceId, Guid AreaId, Guid PositionId);

internal static class PayrollApiSeeds
{
    // El contador es el último grupo completo del id (x12): con dos dígitos, pasado 0xff el Guid
    // quedaba con un dígito de más. Arranca en 0x1000 para no chocar con los ids fijos del harness
    // (…01, …02, …f0, …fe, …ff). El slug del tenant sale de ese grupo.
    private static int _tenantCounter = 0x1000;

    public static readonly string[] AllPermissions =
    [
        PayrollPermissions.PeriodRead, PayrollPermissions.PeriodManage, PayrollPermissions.PeriodApprove,
        PayrollPermissions.SettingsManage,
        HrPermissions.EmployeeRead, HrPermissions.EmployeeManage, HrPermissions.ContractRead, HrPermissions.ContractManage,
        HrPermissions.SettingsManage,
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Un tenant nuevo, con el reloj en <see cref="PayrollApiFixture.DefaultNow"/>.</summary>
    public static async Task<PayrollTenant> NewTenantAsync(PayrollApiFixture fixture, IEnumerable<TenantModuleKey>? modules = null)
    {
        fixture.Clock.UtcNow = PayrollApiFixture.DefaultNow;
        fixture.StripAssignmentsOf = null;
        var idText = $"01900000-0000-7000-8000-{Interlocked.Increment(ref _tenantCounter):x12}";
        await SeedTenantAsync(fixture.Factory, idText, modules);
        var payroll = CreateClient(fixture.Factory, SubjectId, idText, AllPermissions);
        var admin = CreateClient(fixture.Factory, SubjectId, idText, "companies.company.read", "companies.company.manage");
        return new PayrollTenant(fixture, idText, payroll, admin);
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

    /// <summary>Un <c>POST</c> sin cuerpo, con <c>If-Match</c> opcional.</summary>
    public static async Task<HttpResponseMessage> PostAsync(HttpClient client, string url, string? ifMatch, object? body = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return await client.SendAsync(request, Ct);
    }

    public static string CodeOf(JsonElement problem) => problem.GetProperty("code").GetString()!;

    public static bool HasError(JsonElement problem, string key) =>
        problem.TryGetProperty("errors", out var errors) && errors.TryGetProperty(key, out _);

    /// <summary>Una empresa con su sede y su centro: lo que el alta de Hr necesita para un lugar de trabajo.</summary>
    public static async Task<SeededCompany> SeedCompanyAsync(
        PayrollTenant tenant, string companyName, string siteName, string workplaceName, string riskClass = "III")
    {
        var company = await CreateCompanyAsync(tenant.Admin, tenant.Id, companyName);
        var site = await CreatedAsync(
            tenant.Payroll, $"{tenant.HrBase}/sites",
            new { companyId = company.Id, name = siteName, address = (string?)null, cityId = (Guid?)null, compensationFund = "COMFAMA" });
        var siteId = site.GetProperty("id").GetGuid();
        var workplace = await CreatedAsync(
            tenant.Payroll, $"{tenant.HrBase}/workplaces", new { name = workplaceName, siteId, ciiuCode = "4711", riskClass, highRiskPension = false });
        return new SeededCompany(company, siteId, workplace.GetProperty("id").GetGuid());
    }

    /// <summary>Empresa «Comercializadora QEP S.A.S.», sede «Principal», centro «Bodega», área «Operaciones» y cargo «Analista».</summary>
    public static async Task<SeededSetup> SeedSetupAsync(PayrollTenant tenant)
    {
        var first = await SeedCompanyAsync(tenant, "Comercializadora QEP S.A.S.", "Principal", "Bodega");
        var area = await CreatedAsync(tenant.Payroll, $"{tenant.HrBase}/areas", new { name = "Operaciones" });
        var position = await CreatedAsync(tenant.Payroll, $"{tenant.HrBase}/positions", new { name = "Analista" });
        return new SeededSetup(first.Company, first.SiteId, first.WorkplaceId, area.GetProperty("id").GetGuid(), position.GetProperty("id").GetGuid());
    }

    /// <summary>Otra empresa con su sede «Norte» y su centro «Planta».</summary>
    public static Task<SeededCompany> SeedSecondCompanyAsync(PayrollTenant tenant) =>
        SeedCompanyAsync(tenant, "Servicios del Norte S.A.S.", "Norte", "Planta");

    /// <summary>
    /// El cuerpo de un alta (<c>POST /hr/employees</c>): esa empresa, <c>Employee</c>, <c>Indefinite</c>, <c>Monthly</c>, desde
    /// 2026-01-15, 2 500 000 y documento <c>CC</c>. Un diccionario: cada prueba cambia lo suyo.
    /// </summary>
    public static Dictionary<string, object?> HireBody(
        SeededSetup setup,
        Guid? companyId = null,
        string contractType = "Employee",
        string duration = "Indefinite",
        string salaryType = "Monthly",
        string startDate = "2026-01-15",
        string? endDate = null,
        decimal amount = 2_500_000m,
        string documentNumber = "1020304050",
        bool foreignPensionExempt = false,
        Guid? workplaceId = null,
        string firstName = "Ana",
        string lastName = "Pérez",
        string[]? restDays = null) => new()
        {
            ["firstName"] = firstName,
            ["lastName"] = lastName,
            ["documentType"] = "CC",
            ["documentNumber"] = documentNumber,
            ["country"] = null,
            ["foreignPensionExempt"] = foreignPensionExempt,
            ["restDays"] = restDays,
            ["cityId"] = null,
            ["memberId"] = null,
            ["payment"] = null,
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
                    ["documentFileId"] = null,
                },
                ["salary"] = new Dictionary<string, object?>
                {
                    ["amount"] = amount,
                    ["teleworker"] = false,
                    ["acknowledgeBelowMinimum"] = false,
                },
                ["assignment"] = new Dictionary<string, object?>
                {
                    ["positionId"] = setup.PositionId,
                    ["areaId"] = setup.AreaId,
                    ["workplaceId"] = workplaceId ?? setup.WorkplaceId,
                },
            },
        };

    /// <summary>Da de alta por <c>POST /hr/employees</c> y espera 201: devuelve el <c>EmployeeDetailDto</c>.</summary>
    public static async Task<JsonElement> HireAsync(PayrollTenant tenant, Dictionary<string, object?> body)
    {
        var response = await tenant.Payroll.PostAsJsonAsync($"{tenant.HrBase}/employees", body, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await BodyAsync(response));
        return await JsonOfAsync(response);
    }

    public static Task<JsonElement> HireAsync(PayrollTenant tenant, SeededSetup setup, string documentNumber = "1020304050") =>
        HireAsync(tenant, HireBody(setup, documentNumber: documentNumber));

    public static Guid EmployeeIdOf(JsonElement detail) => detail.GetProperty("employee").GetProperty("id").GetGuid();
}
