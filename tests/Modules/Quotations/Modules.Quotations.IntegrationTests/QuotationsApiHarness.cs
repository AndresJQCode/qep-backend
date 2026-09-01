using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Catalog.Application;
using Modules.Customers.Application;
using Modules.Quotations.Application;
using Modules.Storage.Application;
using Testcontainers.PostgreSql;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// El arranque compartido de las pruebas de integracion del modulo, mismo criterio que
/// CustomersApiHarness/CatalogApiFactory: una copia por archivo hace que ajustar una deje a las
/// demas probando contra otra configuracion sin que nada avise.
///
/// Dos cosas lo distinguen de Customers/Catalog:
///
/// 1. Sembrar una cotizacion exige datos de **dos** modulos externos (un cliente de Customers con
///    CUC activo, un producto de Catalog con escalas de precio) -- este harness sabe crearlos via
///    sus propios endpoints HTTP, porque los tres modulos viven en el mismo host de
///    <see cref="QepApiFactory"/>.
/// 2. `advisor_id`/`created_by` resuelven una <c>Membership</c> **activa de verdad**
///    (<c>IMembershipDirectory.FindActiveMembershipIdAsync</c>, ver Quotations §1.4) -- a
///    diferencia de Catalog/Customers, que solo auditan el subject crudo del header. El stub de
///    desarrollo autoriza por el header <c>X-Permissions</c> sin tocar la base, asi que un tenant
///    con id fijo y un subject inventado no alcanzan: hace falta una membresia <c>Active</c> real.
///    La unica forma de conseguirla sin la vuelta completa de login de Google es
///    <c>POST /api/v1/auth/register-tenant</c> (mismo mecanismo que usa
///    <c>MembershipLifecycleApiTests</c> en Tenancy), que deja al dueño ya en <c>Active</c>.
/// </summary>
internal static class QuotationsApiHarness
{
    public static string QuotationsUrl(Guid tenantId) => $"/api/v1/tenants/{tenantId}/quotations";

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

    /// <summary>Los permisos que necesita un cliente para sembrar (cliente, producto) y ejercer
    /// el modulo bajo prueba de punta a punta.</summary>
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
        CatalogPermissions.TaxRateRead,
        CatalogPermissions.TaxRateManage,
        StoragePermissions.FileUpload,
        StoragePermissions.FileRead
    ];

    /// <summary>Registra un tenant nuevo (signup publico) para conseguir una Membership de dueño
    /// ya en estado Active, y devuelve un cliente autenticado como ese dueño con los permisos
    /// pedidos.</summary>
    public static async Task<(Guid TenantId, Guid OwnerUserId, HttpClient Client)> RegisterTenantAsync(
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
                displayName = "Quotations Test Org",
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
        return (registered.TenantId, registered.OwnerUserId, client);
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

    // name/prefix quedan en null por defecto y se generan unicos por llamada: nombre y prefijo
    // de clasificacion son unicos por tenant, y varias pruebas (p. ej. filtros del listado)
    // necesitan mas de un cliente -- y por lo tanto mas de una clasificacion -- en el mismo
    // tenant.
    public static async Task<Guid> CreateClassificationAsync(
        HttpClient client, Guid tenantId, string? name = null, string? prefix = null)
    {
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/customers/classifications",
            new { name = name ?? $"Mediano-{suffix}", prefix = prefix ?? $"C{suffix}" },
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<ClassificationResponseDto>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        return body.Id;
    }

    /// <summary>Da de alta un cliente con CUC activo y devuelve su id -- la referencia blanda que
    /// consume <c>IQuotationCustomerLookup</c>. La identificacion es unica por tenant, asi que
    /// tambien se genera distinta en cada llamada por defecto.</summary>
    public static async Task<Guid> CreateActiveCustomerAsync(
        HttpClient client, Guid tenantId, string? identificationNumber = null)
    {
        var cityId = await EnsureCityIdAsync(client);
        var classificationId = await CreateClassificationAsync(client, tenantId);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/customers",
            new
            {
                name = "Verde Esencial S.A.S.",
                identificationType = "NIT",
                identificationNumber = identificationNumber
                    ?? $"900.{Random.Shared.Next(100, 999)}.{Random.Shared.Next(100, 999)}-1",
                cityId,
                classificationId,
                withRetention = false
            },
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<CustomerResponseDto>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        return body.Id;
    }

    public static async Task DeactivateCustomerAsync(HttpClient client, Guid tenantId, Guid customerId)
    {
        var response = await client.PostAsync(
            $"/api/v1/tenants/{tenantId}/customers/{customerId}/deactivate",
            content: null,
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Da de alta un producto activo con precio base en COP y, salvo que se pidan escalas
    /// propias, las tres del ejemplo del propio documento (1-9 sin descuento, 10-19 5%, 20+ 10%).
    /// <paramref name="taxRateId"/> es opcional -- un producto sin tasa de impuesto asignada
    /// cotiza con 0% (RN-013).
    /// </summary>
    public static async Task<Guid> CreateProductWithScalesAsync(
        HttpClient client,
        Guid tenantId,
        decimal baseCop = 100_000m,
        object[]? scales = null,
        Guid? taxRateId = null)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/catalog/products",
            new
            {
                name = "Vela de soja",
                code = $"VS-{Guid.NewGuid():N}"[..12],
                taxRateId,
                pricing = new
                {
                    baseCop,
                    scales = scales ?? DefaultScales(baseCop)
                }
            },
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<ProductResponseDto>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        return body.Id;
    }

    /// <summary>Da de alta una tasa de impuesto de Catalog, para probar el impuesto por línea
    /// (RN-013) sin depender de otro archivo de pruebas.</summary>
    public static async Task<Guid> CreateTaxRateAsync(
        HttpClient client, Guid tenantId, string name, int percentage)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/catalog/tax-rates",
            new { name, percentage },
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<TaxRateResponseDto>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        return body.Id;
    }

    private static object[] DefaultScales(decimal baseCop) =>
    [
        new
        {
            fromUnit = 1, toUnit = 9, discount = 0m,
            restriction = "multiple", multiple = 1, finalCop = baseCop
        },
        new
        {
            fromUnit = 10, toUnit = 19, discount = 5m,
            restriction = "multiple", multiple = 1, finalCop = baseCop * 0.95m
        },
        new
        {
            fromUnit = 20, toUnit = 999_999, discount = 10m,
            restriction = "multiple", multiple = 1, finalCop = baseCop * 0.90m
        }
    ];

    /// <summary>Un producto cuyas escalas dejan un hueco a proposito (sólo cubren 10-19), para
    /// probar la decision confirmada de "cantidad fuera de cualquier escala -> 0%".</summary>
    public static Task<Guid> CreateProductWithGapInScalesAsync(
        HttpClient client, Guid tenantId, decimal baseCop = 100_000m) =>
        CreateProductWithScalesAsync(
            client,
            tenantId,
            baseCop,
            [
                new
                {
                    fromUnit = 10, toUnit = 19, discount = 5m,
                    restriction = "multiple", multiple = 1, finalCop = baseCop * 0.95m
                }
            ]);

    /// <summary>
    /// Sube un archivo real a Storage (misma sesión de carga firmada que ya usa el resto del
    /// backend) y lo deja en <c>Available</c>. Devuelve su id. Necesita
    /// <see cref="QepApiFactory.ObjectStorage"/> del mismo <paramref name="factory"/> que sirvió
    /// <paramref name="client"/>.
    /// </summary>
    public static async Task<Guid> CreateAvailableFileAsync(
        HttpClient client, QepApiFactory factory, Guid tenantId,
        string mimeType, byte[] payload, string fileName)
    {
        var sessionResponse = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/files",
            new
            {
                ownerId = Guid.NewGuid(),
                ownerType = "User",
                name = fileName,
                mimeType,
                sizeBytes = payload.Length,
            },
            TestContext.Current.CancellationToken);
        sessionResponse.EnsureSuccessStatusCode();
        var session = await sessionResponse.Content.ReadFromJsonAsync<UploadSessionResponseDto>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(session);

        // Simula que R2 acepta los bytes por la URL prefirmada -- mismo mecanismo que
        // StorageFlowTests en el propio módulo Storage.
        factory.ObjectStorage.Upload(session.StorageKey, payload);

        var completeResponse = await client.PostAsync(
            $"/api/v1/tenants/{tenantId}/files/{session.FileResourceId}/complete",
            content: null,
            TestContext.Current.CancellationToken);
        completeResponse.EnsureSuccessStatusCode();

        return session.FileResourceId;
    }

    /// <summary>US-12: un PDF disponible, del tamaño mínimo con firma binaria válida -- lo que
    /// <c>SendQuotationRequest.PdfFileId</c> espera.</summary>
    public static Task<Guid> CreateAvailablePdfFileAsync(
        HttpClient client, QepApiFactory factory, Guid tenantId) =>
        CreateAvailableFileAsync(
            client, factory, tenantId, "application/pdf", "%PDF-1.7\nquotation"u8.ToArray(), "quotation.pdf");

    /// <summary>US-14: un comprobante de pago disponible. PDF y no JPG/PNG a propósito -- los
    /// tres tipos son válidos para <c>SalePaymentProofResolver</c>, pero construir un JPG/PNG
    /// minúsculo que además pase la verificación de firma binaria real de Storage es frágil; un
    /// PDF mínimo válido ya lo tiene <see cref="CreateAvailablePdfFileAsync"/>.</summary>
    public static Task<Guid> CreateAvailablePaymentProofFileAsync(
        HttpClient client, QepApiFactory factory, Guid tenantId) =>
        CreateAvailableFileAsync(
            client, factory, tenantId, "application/pdf", "%PDF-1.7\nproof"u8.ToArray(), "proof.pdf");

    /// <summary>Crea una cotización, le agrega un ítem y la marca como enviada -- el punto de
    /// partida que necesita toda prueba de conversión a venta (US-13 exige <c>Sent</c>).</summary>
    public static async Task<QuotationResponse> CreateSentQuotationAsync(
        HttpClient client, QepApiFactory factory, Guid tenantId, Guid clientId, Guid productId)
    {
        var quotation = await CreateQuotationAsync(client, tenantId, clientId);
        await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/items",
            new AddQuotationItemRequest(productId, 1m),
            TestContext.Current.CancellationToken);
        var pdfFileId = await CreateAvailablePdfFileAsync(client, factory, tenantId);
        var response = await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/send",
            new SendQuotationRequest(pdfFileId),
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var sent = await response.Content.ReadFromJsonAsync<QuotationResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(sent);
        return sent;
    }

    public static async Task<QuotationResponse> CreateQuotationAsync(
        HttpClient client, Guid tenantId, Guid clientId)
    {
        var response = await client.PostAsJsonAsync(
            QuotationsUrl(tenantId),
            new CreateQuotationRequest(clientId, null, null, null, null),
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<QuotationResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        return body;
    }

    private sealed record RegisterTenantResponseDto(Guid TenantId, Guid OwnerUserId);

    private sealed record GeographyDepartmentDto(Guid Id, string DivipolaCode, string Name);

    private sealed record GeographyCityDto(Guid Id, string DivipolaCode, string Name, Guid DepartmentId);

    private sealed record ClassificationResponseDto(Guid Id, string Name, string Prefix);

    private sealed record CustomerResponseDto(Guid Id, string Cuc, bool IsActive);

    private sealed record ProductResponseDto(Guid Id);

    private sealed record TaxRateResponseDto(Guid Id);

    private sealed record UploadSessionResponseDto(Guid FileResourceId, string UploadUrl, string StorageKey);

    public sealed class QepApiFactory(string connectionString)
        : WebApplicationFactory<Program>
    {
        /// <summary>Doble de <c>IObjectStorage</c> en memoria, mismo mecanismo que
        /// StorageFlowTests en el propio módulo Storage: la subida real a R2 no existe en un
        /// test, así que este harness sustituye la implementación real por una que guarda los
        /// bytes en un diccionario.</summary>
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
            // Fijado, nunca heredado de appsettings.json: con "infobip" y las claves de Infobip
            // ausentes, NotificationsOptionsValidator falla al arrancar y todas las pruebas de
            // este proyecto mueren antes de llegar a su asercion. SDD-CT-17.
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
