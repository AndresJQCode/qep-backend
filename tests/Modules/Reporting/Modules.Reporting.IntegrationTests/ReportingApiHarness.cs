using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Catalog.Application;
using Modules.Customers.Application;
using Modules.Quotations.Application;
using Modules.Reporting.Application;
using Modules.Storage.Application;
using Testcontainers.PostgreSql;

namespace Modules.Reporting.IntegrationTests;

/// <summary>
/// El arranque compartido de las pruebas de integracion de Reporting. Una copia por modulo, no
/// por archivo: ajustar una configuracion en un solo lugar es justamente lo que hace que los
/// cuatro reportes se prueben contra el mismo host.
///
/// Reporting no escribe nada, asi que **todo lo que lee lo tiene que sembrar otro modulo**. Y
/// sembrarlo por SQL no alcanzaria: <c>advisorName</c> se resuelve
/// <c>Quotation.AdvisorId</c> → <c>Membership.UserId</c> → <c>User.Email</c>, y eso exige una
/// membresia <c>Active</c> de verdad. La unica forma de conseguirla sin la vuelta completa de
/// login de Google es <c>POST /api/v1/auth/register-tenant</c>, igual que hace el harness de
/// Quotations.
/// </summary>
internal static class ReportingApiHarness
{
    public static string ReportsUrl(Guid tenantId) => $"/api/v1/tenants/{tenantId}/reports";

    /// <summary>El MIME oficial de .xlsx, el mismo que fija el contrato de estos endpoints.</summary>
    public const string ExcelContentType =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    /// <summary>
    /// Todo lo que hace falta para sembrar (cliente, producto, cotizacion, venta, cambio de
    /// precio) **y ademas** leer los cuatro reportes.
    ///
    /// El stub de desarrollo concede solo los permisos de tenancy por defecto, asi que cada uno
    /// de estos tiene que viajar en <c>X-Permissions</c> — si no, el 403 que devuelve una prueba
    /// viene del permiso de siembra que falta y no de lo que cree estar probando.
    /// </summary>
    public static readonly string[] ManagerPermissions =
    [
        QuotationsPermissions.QuotationRead,
        QuotationsPermissions.QuotationManage,
        SalesPermissions.SaleRead,
        SalesPermissions.SaleManage,
        CustomersPermissions.CustomerRead,
        CustomersPermissions.CustomerManage,
        CustomersPermissions.ClassificationRead,
        CustomersPermissions.ClassificationManage,
        CatalogPermissions.ProductRead,
        CatalogPermissions.ProductManage,
        StoragePermissions.FileUpload,
        StoragePermissions.FileRead,
        ReportingPermissions.SalesRead,
        ReportingPermissions.QuotationRead,
        ReportingPermissions.PriceChangeRead,
        ReportingPermissions.CustomerRead
    ];

    /// <summary>Los mismos permisos de siembra, sin **ninguno** de los cuatro de Reporting: es lo
    /// que necesita una prueba de 403 por permiso faltante para que el 403 venga del permiso que
    /// se esta probando.</summary>
    public static readonly string[] SeedOnlyPermissions =
        [.. ManagerPermissions.Where(permission =>
            !permission.StartsWith("reporting.", StringComparison.Ordinal))];

    public static async Task<PostgreSqlContainer> StartDatabaseAsync()
    {
        var database = new PostgreSqlBuilder("postgres:18-alpine")
            .WithDatabase("qep")
            .WithUsername("qep")
            .WithPassword("qep-integration")
            .Build();
        await database.StartAsync(TestContext.Current.CancellationToken);
        return database;
    }

    public static HttpClient CreateClient(
        QepApiFactory factory,
        string subjectId,
        string tenantId,
        params string[] permissions)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Subject-Id", subjectId);
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenantId);
        if (permissions.Length > 0)
        {
            client.DefaultRequestHeaders.Add("X-Permissions", string.Join(',', permissions));
        }

        return client;
    }

    /// <summary>Registra un tenant nuevo para conseguir una Membership de dueño ya en Active, y
    /// devuelve un cliente autenticado como ese dueño. <c>OwnerEmail</c> vuelve porque es el
    /// valor que los reportes muestran en <c>advisorName</c>/<c>changedByName</c>: el sistema no
    /// guarda nombre de persona en ningun lado.</summary>
    public static async Task<RegisteredTenant> RegisterTenantAsync(
        QepApiFactory factory, params string[] permissions)
    {
        var email = $"owner-{Guid.CreateVersion7():N}@example.com";
        using var bootstrap = CreateClient(
            factory, Guid.CreateVersion7().ToString(), Guid.CreateVersion7().ToString());
        bootstrap.DefaultRequestHeaders.Add("X-Email", email);
        bootstrap.DefaultRequestHeaders.Add("X-Email-Verified", "true");

        var response = await bootstrap.PostAsJsonAsync(
            "/api/v1/auth/register-tenant",
            new
            {
                displayName = "Reporting Test Org",
                slug = $"org-{Guid.NewGuid():N}"[..12],
                defaultCulture = "es-CO",
                timeZone = "America/Bogota",
                dateFormat = "yyyy-MM-dd",
            },
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var registered = await response.Content.ReadFromJsonAsync<RegisterTenantResponseDto>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(registered);

        var client = CreateClient(
            factory, registered.OwnerUserId.ToString(), registered.TenantId.ToString(), permissions);
        return new RegisteredTenant(registered.TenantId, registered.OwnerUserId, email, client);
    }

    public static async Task<Guid> EnsureCityIdAsync(HttpClient client)
    {
        var departments = await client.GetFromJsonAsync<List<GeographyDepartmentDto>>(
            "/api/v1/departments", TestContext.Current.CancellationToken);
        Assert.NotNull(departments);
        Assert.NotEmpty(departments);

        foreach (var department in departments)
        {
            var cities = await client.GetFromJsonAsync<List<GeographyCityDto>>(
                $"/api/v1/cities?departmentId={department.Id}",
                TestContext.Current.CancellationToken);
            if (cities is { Count: > 0 })
            {
                return cities[0].Id;
            }
        }

        throw new InvalidOperationException(
            "No seeded DIVIPOLA department has at least one city.");
    }

    public static async Task<Guid> CreateClassificationAsync(HttpClient client, Guid tenantId)
    {
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/customers/classifications",
            new { name = $"Mediano-{suffix}", prefix = $"C{suffix}" },
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<ClassificationResponseDto>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        return body.Id;
    }

    public static async Task<SeededCustomer> CreateActiveCustomerAsync(
        HttpClient client, Guid tenantId)
    {
        var cityId = await EnsureCityIdAsync(client);
        var classificationId = await CreateClassificationAsync(client, tenantId);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/customers",
            new
            {
                name = "Verde Esencial S.A.S.",
                identificationType = "NIT",
                identificationNumber =
                    $"900.{Random.Shared.Next(100, 999)}.{Random.Shared.Next(100, 999)}-1",
                cityId,
                classificationId,
                withRetention = false
            },
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<CustomerResponseDto>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        return new SeededCustomer(body.Id, body.Cuc, classificationId, cityId);
    }

    public static async Task<Guid> CreateProductAsync(
        HttpClient client, Guid tenantId, decimal baseCop = 100_000m)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/catalog/products",
            new
            {
                name = "Vela de soja",
                code = $"VS-{Guid.NewGuid():N}"[..12],
                pricing = new
                {
                    baseCop,
                    scales = new object[]
                    {
                        new
                        {
                            fromUnit = 1, toUnit = 999_999, discount = 0m,
                            restriction = "multiple", multiple = 1, finalCop = baseCop
                        }
                    }
                }
            },
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<ProductResponseDto>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        return body.Id;
    }

    /// <summary>Cambia el precio base en COP de un producto, que es lo que deja una fila en
    /// <c>catalog.product_price_changes</c> — el unico origen del reporte de cambios de
    /// precio.</summary>
    public static async Task ChangeProductBaseCopAsync(
        HttpClient client, Guid tenantId, Guid productId, decimal newBaseCop)
    {
        var current = await client.GetFromJsonAsync<ProductDetailResponseDto>(
            $"/api/v1/tenants/{tenantId}/catalog/products/{productId}",
            TestContext.Current.CancellationToken);
        Assert.NotNull(current);

        var response = await client.PutAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/catalog/products/{productId}",
            new
            {
                name = current.Name,
                code = current.Code,
                pricing = new
                {
                    baseCop = newBaseCop,
                    scales = new object[]
                    {
                        new
                        {
                            fromUnit = 1, toUnit = 999_999, discount = 0m,
                            restriction = "multiple", multiple = 1, finalCop = newBaseCop
                        }
                    }
                }
            },
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public static async Task<QuotationResponse> CreateSentQuotationAsync(
        HttpClient client, QepApiFactory factory, Guid tenantId, Guid clientId, Guid productId)
    {
        var created = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/quotations",
            new CreateQuotationRequest(clientId, null, null, null, null, null),
            TestContext.Current.CancellationToken);
        created.EnsureSuccessStatusCode();
        var quotation = await created.Content.ReadFromJsonAsync<QuotationResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(quotation);

        var added = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/quotations/{quotation.Id}/items",
            new AddQuotationItemRequest(productId, 1m),
            TestContext.Current.CancellationToken);
        added.EnsureSuccessStatusCode();

        var pdfFileId = await CreateAvailablePdfFileAsync(client, factory, tenantId);
        var sent = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/quotations/{quotation.Id}/send",
            new SendQuotationRequest(pdfFileId),
            TestContext.Current.CancellationToken);
        sent.EnsureSuccessStatusCode();
        var body = await sent.Content.ReadFromJsonAsync<QuotationResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        return body;
    }

    public static async Task<SaleResponse> ConvertToSaleAsync(
        HttpClient client,
        QepApiFactory factory,
        Guid tenantId,
        QuotationResponse quotation,
        string paymentStatus = "FullPaymentReceived")
    {
        var proofFileId = await CreateAvailablePdfFileAsync(client, factory, tenantId);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/quotations/{quotation.Id}/sale",
            new ConvertQuotationToSaleRequest(
                paymentStatus,
                "Pago verificado",
                [new SalePaymentProofRequest(proofFileId, quotation.Total)]),
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var sale = await response.Content.ReadFromJsonAsync<SaleResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(sale);
        return sale;
    }

    private static async Task<Guid> CreateAvailablePdfFileAsync(
        HttpClient client, QepApiFactory factory, Guid tenantId)
    {
        var payload = "%PDF-1.7\nreporting"u8.ToArray();
        var sessionResponse = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/files",
            new
            {
                ownerId = Guid.NewGuid(),
                ownerType = "User",
                name = "document.pdf",
                mimeType = "application/pdf",
                sizeBytes = payload.Length,
            },
            TestContext.Current.CancellationToken);
        sessionResponse.EnsureSuccessStatusCode();
        var session = await sessionResponse.Content.ReadFromJsonAsync<UploadSessionResponseDto>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(session);

        factory.ObjectStorage.Upload(session.StorageKey, payload);

        var completeResponse = await client.PostAsync(
            $"/api/v1/tenants/{tenantId}/files/{session.FileResourceId}/complete",
            content: null,
            TestContext.Current.CancellationToken);
        completeResponse.EnsureSuccessStatusCode();

        return session.FileResourceId;
    }

    internal sealed record RegisteredTenant(
        Guid TenantId, Guid OwnerUserId, string OwnerEmail, HttpClient Client);

    internal sealed record SeededCustomer(
        Guid Id, string Cuc, Guid ClassificationId, Guid CityId);

    private sealed record RegisterTenantResponseDto(Guid TenantId, Guid OwnerUserId);

    private sealed record GeographyDepartmentDto(Guid Id, string DivipolaCode, string Name);

    private sealed record GeographyCityDto(
        Guid Id, string DivipolaCode, string Name, Guid DepartmentId);

    private sealed record ClassificationResponseDto(Guid Id, string Name, string Prefix);

    private sealed record CustomerResponseDto(Guid Id, string Cuc, bool IsActive);

    private sealed record ProductResponseDto(Guid Id);

    private sealed record ProductDetailResponseDto(Guid Id, string Name, string Code);

    private sealed record UploadSessionResponseDto(
        Guid FileResourceId, string UploadUrl, string StorageKey);

    public sealed class QepApiFactory(string connectionString)
        : WebApplicationFactory<Program>
    {
        public InMemoryObjectStorage ObjectStorage { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:QepDatabase", connectionString);
            builder.UseSetting("OpenTelemetry:Endpoint", string.Empty);
            builder.UseSetting("Storage:R2:AccountId", "test-account");
            builder.UseSetting("Storage:R2:AccessKeyId", "test-access-key");
            builder.UseSetting("Storage:R2:SecretAccessKey", "test-secret");
            builder.UseSetting("Storage:R2:Bucket", "test-bucket");
            // Fijado, nunca heredado de appsettings.json: con "infobip" y las claves ausentes,
            // NotificationsOptionsValidator falla al arrancar y todas las pruebas de este archivo
            // mueren antes de llegar a su asercion. SDD-CT-17.
            builder.UseSetting("Notifications:EmailProvider", "log");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IObjectStorage>();
                services.AddSingleton<IObjectStorage>(ObjectStorage);
            });
        }
    }

    public sealed class InMemoryObjectStorage : IObjectStorage
    {
        private readonly Dictionary<string, byte[]> _objects = new(StringComparer.Ordinal);

        public Task<Uri> CreatePresignedUploadUrlAsync(
            string key, string contentType, CancellationToken cancellationToken) =>
            Task.FromResult(new Uri($"https://r2.test/{key}"));

        public Task<Uri> CreatePresignedDownloadUrlAsync(
            string key, CancellationToken cancellationToken) =>
            Task.FromResult(new Uri($"https://r2.test/{key}"));

        public Task<Uri> CreatePresignedDownloadUrlAsync(
            string key,
            TimeSpan expiry,
            string? downloadFileName,
            CancellationToken cancellationToken) =>
            Task.FromResult(new Uri($"https://r2.test/{key}"));

        public Task<StoredObject?> StatAsync(string key, CancellationToken cancellationToken)
        {
            if (!_objects.TryGetValue(key, out var content))
            {
                return Task.FromResult<StoredObject?>(null);
            }

            var checksum = Convert.ToHexStringLower(SHA256.HashData(content));
            return Task.FromResult<StoredObject?>(new StoredObject(content.LongLength, checksum));
        }

        public Task DeleteAsync(string key, CancellationToken cancellationToken)
        {
            _objects.Remove(key);
            return Task.CompletedTask;
        }

        public Task PromoteAsync(
            string sourceKey,
            string destinationKey,
            string expectedChecksum,
            CancellationToken cancellationToken)
        {
            _objects[destinationKey] = _objects[sourceKey].ToArray();
            return Task.CompletedTask;
        }

        public Task<byte[]> DownloadAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(_objects[key].ToArray());

        public Task UploadAsync(
            string key, byte[] content, string contentType, CancellationToken cancellationToken)
        {
            _objects[key] = content.ToArray();
            return Task.CompletedTask;
        }

        public void Upload(string key, byte[] content) => _objects[key] = content.ToArray();
    }
}
