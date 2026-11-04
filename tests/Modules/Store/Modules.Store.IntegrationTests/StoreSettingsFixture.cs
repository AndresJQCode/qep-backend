using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Storage.Application;
using Modules.Storage.Domain;
using TenantModuleKey = Modules.Tenancy.Domain.TenantModuleKey;
using Npgsql;
using Testcontainers.PostgreSql;
using static Modules.Store.IntegrationTests.StoreApiHarness;

namespace Modules.Store.IntegrationTests;

/// <summary>
/// Un tenant listo para configurar su tienda: una empresa con cuentas en COP y EUR, un producto con precio en esas dos monedas
/// (sin él <c>currencies-in-use</c> está vacío) y un almacenamiento público en memoria, que es lo que hace "pública" a una imagen.
/// </summary>
internal sealed class StoreSettingsFixture : IAsyncDisposable
{
    public const string AdminPermissions =
        "store.settings.manage,companies.company.read,companies.company.manage,catalog.product.read,catalog.product.manage";

    public static readonly Guid Tenant = Guid.Parse(TenantId);

    private readonly PostgreSqlContainer _database;

    private StoreSettingsFixture(
        PostgreSqlContainer database, QepApiFactory factory, HttpClient admin, CompanyRef company)
    {
        _database = database;
        Factory = factory;
        Admin = admin;
        Company = company;
    }

    public QepApiFactory Factory { get; }

    /// <summary>Quien configura la tienda: <c>store.settings.manage</c> más lo que hace falta para sembrar empresas y productos.</summary>
    public HttpClient Admin { get; }

    /// <summary>Banco "Bancolombia" con A-COP-1 en COP y "Davivienda" con B-EUR-1 en EUR.</summary>
    public CompanyRef Company { get; }

    public static async Task<StoreSettingsFixture> OpenAsync(IEnumerable<TenantModuleKey>? modules = null)
    {
        var database = await StartDatabaseAsync();
        var factory = new QepApiFactory(
            database.GetConnectionString(),
            services =>
            {
                services.RemoveAll<IPublicObjectStorage>();
                services.AddSingleton<IPublicObjectStorage>(new InMemoryPublicStorage());
            });
        await SeedTenantAsync(factory, modules: modules);

        var admin = CreateClient(factory, SubjectId, TenantId, AdminPermissions);
        var company = await CreateCompanyAsync(
            admin,
            Tenant,
            "Origen Botánico SAS",
            ("Bancolombia", "A-COP-1", "COP"),
            ("Davivienda", "B-EUR-1", "EUR"));
        await CreateProductAsync(
            admin, Tenant, "CAM-001", "Camiseta", new Dictionary<string, decimal> { ["COP"] = 50_000m, ["EUR"] = 12m });
        return new StoreSettingsFixture(database, factory, admin, company);
    }

    public HttpClient ClientFor(string tenantId, params string[] permissions) =>
        CreateClient(Factory, SubjectId, tenantId, permissions);

    public async ValueTask DisposeAsync()
    {
        Admin.Dispose();
        Factory.Dispose();
        await _database.DisposeAsync();
    }

    /// <summary>El cuerpo mínimo y válido del <c>PUT</c>; cada prueba pisa lo que le interesa.</summary>
    public Dictionary<string, object?> Body(Action<Dictionary<string, object?>>? tweak = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["slug"] = "tienda-demo",
            ["name"] = null,
            ["primaryColor"] = "#FFD400",
            ["onPrimaryColor"] = null,
            ["accentTextColor"] = null,
            ["bannerFileId"] = null,
            ["bannerText"] = null,
            ["dataPolicyUrl"] = "https://demo.test/politica",
            ["pricingMode"] = "RetailOnly",
            ["currencies"] = new[] { CurrencyEntry("COP", "Bancolombia", "A-COP-1") },
            ["primaryCurrency"] = "COP",
            ["salesCountries"] = new[] { "CO" },
            ["featuredCategoryIds"] = Array.Empty<Guid>(),
        };
        tweak?.Invoke(body);
        return body;
    }

    public object CurrencyEntry(string currency, string bank, string account, string? accountCurrency = null, Guid? companyId = null) =>
        new
        {
            currency,
            billingAccount = new
            {
                companyId = companyId ?? Company.Id,
                bankName = bank,
                accountNumber = account,
                currency = accountCurrency ?? currency,
            },
        };

    public async Task<HttpResponseMessage> PutAsync(
        object body, string? ifMatch = "0", HttpClient? client = null, string tenantId = StoreApiHarness.TenantId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"{StoreUrl(tenantId)}/settings")
        {
            Content = JsonContent.Create(body),
        };
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", $"\"{ifMatch}\"");
        }

        request.Headers.Add("X-Qep-Client", "web");
        return await (client ?? Admin).SendAsync(request, TestContext.Current.CancellationToken);
    }

    public async Task<HttpResponseMessage> PostAsync(string path, string? ifMatch, object? body = null, HttpClient? client = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{StoreUrl()}/settings{path}")
        {
            Content = body is null ? null : JsonContent.Create(body),
        };
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", $"\"{ifMatch}\"");
        }

        request.Headers.Add("X-Qep-Client", "web");
        return await (client ?? Admin).SendAsync(request, TestContext.Current.CancellationToken);
    }

    public Task<HttpResponseMessage> GetAsync(string path = "/settings", HttpClient? client = null, string tenantId = StoreApiHarness.TenantId) =>
        (client ?? Admin).GetAsync($"{StoreUrl(tenantId)}{path}", TestContext.Current.CancellationToken);

    /// <summary>Guarda la tienda con el cuerpo dado y exige 200; devuelve el JSON.</summary>
    public async Task<JsonNode> SaveAsync(Dictionary<string, object?> body, string ifMatch = "0")
    {
        var response = await PutAsync(body, ifMatch);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        return await JsonAsync(response);
    }

    public static async Task<JsonNode> JsonAsync(HttpResponseMessage response) =>
        JsonNode.Parse(await BodyAsync(response))!;

    public static async Task<string> CodeAsync(HttpResponseMessage response) =>
        (string)(await JsonAsync(response))["code"]!;

    /// <summary>Una imagen de cualquier dueño, ya disponible; con <paramref name="published"/> también en el bucket público.</summary>
    public async Task<Guid> SeedImageAsync(FileOwnerType owner, bool published)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IFileResourceRepository>();
        var now = DateTimeOffset.UtcNow;
        var file = FileResource.CreatePendingUpload(
            FileResourceId.New(), Tenant, Tenant, owner, "banner.png", "image/png", 1024, $"staging/{Guid.CreateVersion7():N}", now);
        file.CompleteUpload("checksum", 1024, now);
        file.MarkClean(now);
        if (published)
        {
            file.Publish($"public/{Guid.CreateVersion7():N}.png", now);
        }

        repository.Add(file);
        await scope.ServiceProvider.GetRequiredService<IStorageUnitOfWork>().SaveChangesAsync(TestContext.Current.CancellationToken);
        return file.Id.Value;
    }

    public async Task<List<string>> SubdomainHostnamesAsync(Guid tenant)
    {
        var connectionString = Factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("QepDatabase")!;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT hostname FROM store.store_domains WHERE tenant_id = @tenant ORDER BY hostname", connection);
        command.Parameters.AddWithValue("tenant", tenant);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var hostnames = new List<string>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            hostnames.Add(reader.GetString(0));
        }

        return hostnames;
    }
}

/// <summary>El bucket público en memoria: sólo responde que está configurado y arma URLs, porque no se sube nada de verdad.</summary>
internal sealed class InMemoryPublicStorage : IPublicObjectStorage
{
    public const string BaseUrl = "https://assets.qep.test";

    public bool IsConfigured => true;

    public Task CopyFromPrivateAsync(string privateKey, string publicKey, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task DeleteAsync(string publicKey, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<bool> ExistsAsync(string publicKey, CancellationToken cancellationToken) => Task.FromResult(true);

    public string GetUrl(string publicKey) => $"{BaseUrl}/{publicKey}";

    public Task<PublicObjectPage> ListAsync(string prefix, string? continuationToken, CancellationToken cancellationToken) =>
        Task.FromResult(new PublicObjectPage([], null));
}
