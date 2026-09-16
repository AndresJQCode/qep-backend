using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Modules.Catalog.Application;
using Modules.Companies.Application;
using Modules.Customers.Application;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Quotations.Infrastructure.Exports;
using Modules.Quotations.Infrastructure.Persistence;
using Modules.Storage.Application;
using Npgsql;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
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
        OrdersPermissions.OrderRead,
        OrdersPermissions.OrderManage,
        CustomersPermissions.CustomerRead,
        CustomersPermissions.CustomerManage,
        CustomersPermissions.ClassificationRead,
        CustomersPermissions.ClassificationManage,
        CatalogPermissions.ProductRead,
        CatalogPermissions.ProductManage,
        CatalogPermissions.TaxRateRead,
        CatalogPermissions.TaxRateManage,
        StoragePermissions.FileUpload,
        StoragePermissions.FileRead,
        // Desde que `Quotation.EnsureComplete` exige cuenta de cobro para enviar, armar una
        // cotización enviable pasa por la API de Companies.
        CompaniesPermissions.CompanyRead,
        CompaniesPermissions.CompanyManage
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
        HttpClient client,
        Guid tenantId,
        string? identificationNumber = null,
        bool withRetention = false,
        bool vatSurplus = false)
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
                address = "Calle 10 # 45-12",
                cityId,
                classificationId,
                withRetention,
                vatSurplus
            },
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<CustomerResponseDto>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        return body.Id;
    }

    /// <summary>Prende o apaga retención/excedente de IVA de un cliente ya creado -- para probar
    /// que una cotización todavía editable (Draft/Sent) se entera del cambio sin haber sido
    /// creada de nuevo (Quotation.RefreshCustomerTaxProfile). El resto de los campos se repiten
    /// tal cual porque <c>UpdateCustomerRequest</c> reemplaza el recurso entero.</summary>
    public static async Task UpdateCustomerRetentionAsync(
        HttpClient client,
        Guid tenantId,
        Guid customerId,
        string identificationNumber,
        bool withRetention,
        bool vatSurplus)
    {
        var cityId = await EnsureCityIdAsync(client);
        var classificationId = await CreateClassificationAsync(client, tenantId);

        var response = await client.PutAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/customers/{customerId}",
            new
            {
                name = "Verde Esencial S.A.S.",
                identificationType = "NIT",
                identificationNumber,
                address = "Calle 10 # 45-12",
                cityId,
                classificationId,
                withRetention,
                vatSurplus
            },
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
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
        string mimeType, byte[] payload, string fileName, string ownerType = "User")
    {
        var sessionResponse = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/files",
            new
            {
                ownerId = Guid.NewGuid(),
                ownerType,
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
    /// tres tipos son válidos para <c>OrderPaymentProofResolver</c>, pero construir un JPG/PNG
    /// minúsculo que además pase la verificación de firma binaria real de Storage es frágil; un
    /// PDF mínimo válido ya lo tiene <see cref="CreateAvailablePdfFileAsync"/>.</summary>
    public static Task<Guid> CreateAvailablePaymentProofFileAsync(
        HttpClient client, QepApiFactory factory, Guid tenantId) =>
        CreateAvailableFileAsync(
            client, factory, tenantId, "application/pdf", "%PDF-1.7\nproof"u8.ToArray(), "proof.pdf");

    /// <summary>Spec 2026-09-16: un comprobante v2 (<c>PaymentProof</c>) de imagen. Storage lo deja
    /// en WebP y en staging/ al completarlo (D7, D8), y lo mueve al público cuando se adjunta (D9).
    /// </summary>
    public static async Task<Guid> CreateAvailablePaymentProofImageAsync(
        HttpClient client, QepApiFactory factory, Guid tenantId)
    {
        using var image = new Image<Rgba32>(64, 48, Color.CornflowerBlue);
        await using var png = new MemoryStream();
        await image.SaveAsPngAsync(png, TestContext.Current.CancellationToken);
        return await CreateAvailableFileAsync(
            client, factory, tenantId, "image/png", png.ToArray(), "comprobante.png", ownerType: "PaymentProof");
    }

    /// <summary>Crea una cotización, le agrega un ítem y la marca como enviada -- el punto de
    /// partida que necesita toda prueba de conversión a pedido (US-13 exige <c>Sent</c>).</summary>
    /// <summary>
    /// Una empresa con una cuenta bancaria, que es de donde la cotización copia su cuenta de
    /// cobro: <c>QuotationBillingAccountRequest</c> la valida contra las cuentas de la empresa
    /// antes de copiarla, así que no alcanza con inventar un nombre de banco.
    /// </summary>
    public static async Task<(Guid CompanyId, string BankName, string AccountNumber, string Currency)>
        CreateCompanyWithBankAccountAsync(HttpClient client, Guid tenantId)
    {
        var cityId = await EnsureCityIdAsync(client);
        const string bankName = "Bancolombia";
        var accountNumber = $"{Random.Shared.Next(100000000, 999999999)}";
        const string currency = "COP";

        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/companies",
            new
            {
                name = "QEP Comercial S.A.S.",
                bankAccounts = new[]
                {
                    new { bankName, accountNumber, currency },
                },
                taxId = $"901.{Random.Shared.Next(100, 999)}.{Random.Shared.Next(100, 999)}-2",
                cityId,
                phone = "6015550000",
                email = "facturacion@qep.example.co",
                address = "Carrera 7 # 71-21",
            },
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<CompanyResponseDto>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        return (body.Id, bankName, accountNumber, currency);
    }

    public static async Task<QuotationResponse> CreateSentQuotationAsync(
        HttpClient client,
        QepApiFactory factory,
        Guid tenantId,
        Guid clientId,
        Guid productId,
        string? paymentMethod = "Transferencia")
    {
        // Los tres datos que `Quotation.EnsureComplete` exige para enviar (f656ec9): productos,
        // vigencia y cuenta de cobro. La vigencia la pone `CreateQuotationAsync`; las otras dos,
        // acá. Sin la cuenta el envío devuelve 422 `quotation.billing.account_required`, y el
        // error aparece en la aserción de la prueba que llamó a este helper, no acá.
        var billing = await CreateCompanyWithBankAccountAsync(client, tenantId);
        var quotation = await CreateQuotationAsync(
            client,
            tenantId,
            clientId,
            validUntil: null,
            billingAccount: new QuotationBillingAccountRequest(
                billing.CompanyId, billing.BankName, billing.AccountNumber, billing.Currency),
            // Ya no es un requisito para convertir en pedido (2026-09-12: el editor dejó de
            // pedirla, así que exigirla bloqueaba toda cotización nueva). El parámetro se queda
            // por si alguna prueba puntual quiere una cotización con forma de pago cargada.
            paymentMethod: paymentMethod);
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

    /// <summary>Nace con vigencia porque <c>Quotation.Send</c> la exige: sin
    /// <c>ValidUntil</c> la cotización nunca vencería y quedaría convertible a pedido para
    /// siempre. Las pruebas que necesitan otra fecha (el barrido de vencimiento) la
    /// sobrescriben después con <c>UpdateQuotationRequest</c>, que sigue disponible en
    /// <c>Sent</c>.</summary>
    public static async Task<QuotationResponse> CreateQuotationAsync(
        HttpClient client,
        Guid tenantId,
        Guid clientId,
        DateOnly? validUntil = null,
        QuotationBillingAccountRequest? billingAccount = null,
        string? paymentMethod = null)
    {
        var response = await client.PostAsJsonAsync(
            QuotationsUrl(tenantId),
            new CreateQuotationRequest(
                clientId,
                validUntil ?? DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30),
                paymentMethod,
                null,
                null,
                billingAccount),
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<QuotationResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        return body;
    }

    /// <summary>Reemplaza los procesadores de exportación por los de la prueba. Los reales se
    /// sacan primero: dos del mismo kind hacen explotar al runner al construirse.</summary>
    public static WebApplicationFactory<Program> WithExportProcessors(
        this WebApplicationFactory<Program> factory, params IExportJobProcessor[] processors) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IExportJobProcessor>();
            foreach (var processor in processors)
            {
                services.AddSingleton(processor);
            }
        }));

    public static async Task<Guid> EnqueueExportJobAsync(
        WebApplicationFactory<Program> factory,
        Guid tenantId,
        Guid requestedBy,
        ExportJobKind kind = ExportJobKind.Quotations,
        string filters = "{}")
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var job = ExportJob.Enqueue(
            Guid.CreateVersion7(), tenantId, requestedBy, kind, filters, DateTimeOffset.UtcNow);
        scope.ServiceProvider.GetRequiredService<IExportJobQueue>().Add(job);
        await scope.ServiceProvider.GetRequiredService<IQuotationsUnitOfWork>()
            .SaveChangesAsync(TestContext.Current.CancellationToken);
        return job.Id;
    }

    /// <summary>Un tick del worker, a mano: el hosted service no corre en las pruebas (ver
    /// <see cref="QepApiFactory"/>), así que el orden de los ticks lo decide la prueba.</summary>
    public static async Task<ExportJobRunOutcome> RunExportJobAsync(WebApplicationFactory<Program> factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ExportJobRunner>()
            .RunNextAsync(TestContext.Current.CancellationToken);
    }

    public static async Task<ExportJob> FindExportJobAsync(
        WebApplicationFactory<Program> factory, Guid jobId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<QuotationsDbContext>();
        return await dbContext.ExportJobs
            .AsNoTracking()
            .SingleAsync(job => job.Id == jobId, TestContext.Current.CancellationToken);
    }

    /// <summary>Mueve el próximo intento al pasado directo en la base: el backoff es de minutos y
    /// el reloj del host no se corre por prueba. Mismo criterio que BackdateAsync.</summary>
    public static Task MakeExportJobDueAsync(WebApplicationFactory<Program> factory, Guid jobId) =>
        UpdateExportJobAsync(factory, jobId, setters =>
            setters.SetProperty(job => job.NextAttemptAt, DateTimeOffset.UtcNow.AddSeconds(-1)));

    /// <summary>Simula un worker muerto: el lease queda vencido sin que nadie cierre el job.</summary>
    public static Task ExpireExportLeaseAsync(WebApplicationFactory<Program> factory, Guid jobId) =>
        UpdateExportJobAsync(factory, jobId, setters =>
            setters.SetProperty(job => job.LockedUntil, DateTimeOffset.UtcNow.AddSeconds(-1)));

    public static async Task<IReadOnlyList<QuotationsOutboxMessage>> OutboxMessagesAsync(
        WebApplicationFactory<Program> factory, string eventName)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<QuotationsDbContext>();
        return await dbContext.Outbox
            .AsNoTracking()
            .Where(message => message.EventName == eventName)
            .OrderBy(message => message.OccurredAt)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    // Los workers de Notifications sí corren en el host de pruebas: el correo sale solo, y se
    // espera con plazo, como en InvitationNotificationTests.
    public static async Task<string?> WaitForEmailStatusAsync(
        string connectionString, Guid recipientId, string templateRef)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var command = new NpgsqlCommand(
                """
                SELECT status FROM notifications.notifications
                WHERE recipient_id = @recipientId AND template_ref = @templateRef
                """,
                connection);
            command.Parameters.AddWithValue("recipientId", recipientId);
            command.Parameters.AddWithValue("templateRef", templateRef);
            if (await command.ExecuteScalarAsync(TestContext.Current.CancellationToken) is string status)
            {
                return status;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);
        }

        return null;
    }

    private static async Task UpdateExportJobAsync(
        WebApplicationFactory<Program> factory,
        Guid jobId,
        Action<Microsoft.EntityFrameworkCore.Query.UpdateSettersBuilder<ExportJob>> setters)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<QuotationsDbContext>();
        var updated = await dbContext.ExportJobs
            .Where(job => job.Id == jobId)
            .ExecuteUpdateAsync(setters, TestContext.Current.CancellationToken);
        Assert.Equal(1, updated);
    }

    private sealed record RegisterTenantResponseDto(Guid TenantId, Guid OwnerUserId);

    private sealed record GeographyDepartmentDto(Guid Id, string DivipolaCode, string Name);

    private sealed record GeographyCityDto(Guid Id, string DivipolaCode, string Name, Guid DepartmentId);

    private sealed record ClassificationResponseDto(Guid Id, string Name, string Prefix);

    private sealed record CustomerResponseDto(Guid Id, string Cuc, bool IsActive);

    private sealed record CompanyResponseDto(Guid Id, string Name);

    /// <summary>Devuelve una cabecera de PDF valida y nada mas: lo que estas pruebas
    /// verifican es el flujo, no el documento. El contenido del PDF lo cubre
    /// `QCodePdfRendererTests` contra el contrato del servicio.</summary>
    private sealed class StubPdfStorage : IQuotationPdfStorage
    {
        public Task<string> SaveAsync(
            Guid tenantId,
            QuotationId quotationId,
            byte[] content,
            CancellationToken cancellationToken) =>
            Task.FromResult($"quotations/tenants/{tenantId:N}/{Guid.CreateVersion7():N}.pdf");

        public Task<string> PublishAsync(string storageKey, CancellationToken cancellationToken) =>
            Task.FromResult($"https://assets.example.co/{storageKey}");

        public Task<string> CreateDownloadUrlAsync(
            string storageKey, string downloadFileName, CancellationToken cancellationToken) =>
            Task.FromResult($"https://r2.example.com/{storageKey}?X-Amz-Signature=stub");
    }

    private sealed class StubPdfRenderer : IQuotationPdfRenderer
    {
        public Task<byte[]> RenderAsync(
            QuotationPdfDocument document, CancellationToken cancellationToken) =>
            Task.FromResult<byte[]>([0x25, 0x50, 0x44, 0x46]);
    }

    private sealed record ProductResponseDto(Guid Id);

    private sealed record TaxRateResponseDto(Guid Id);

    private sealed record UploadSessionResponseDto(Guid FileResourceId, string UploadUrl, string StorageKey);

    public sealed class QepApiFactory(
        string connectionString, bool runExportWorker = false, bool publicPaymentProofLinks = false)
        : WebApplicationFactory<Program>
    {
        // Copia del flag para ConfigureWebHost. Si ese método leyera el parámetro, que además
        // inicializa PublicObjectStorage, el compilador avisaría CS9124 (parámetro capturado y usado
        // en un inicializador), y con TreatWarningsAsErrors el build falla.
        private readonly bool _publicPaymentProofLinks = publicPaymentProofLinks;

        /// <summary>Doble de <c>IObjectStorage</c> en memoria, mismo mecanismo que
        /// StorageFlowTests en el propio módulo Storage: la subida real a R2 no existe en un
        /// test, así que este harness sustituye la implementación real por una que guarda los
        /// bytes en un diccionario.</summary>
        public InMemoryObjectStorage ObjectStorage { get; } = new();

        /// <summary>Doble de <c>IPublicObjectStorage</c> (spec 2026-09-15): el publicador real de
        /// comprobantes copia entre buckets de R2, que en una prueba no existen. Anota las copias y
        /// los borrados para que la prueba los vea. Queda configurado sólo con
        /// <c>publicPaymentProofLinks</c>, como el adaptador real sólo lo está con bucket público.</summary>
        public InMemoryPublicObjectStorage PublicObjectStorage { get; } = new() { IsConfigured = publicPaymentProofLinks };

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

            // Fijado, nunca heredado, mismo criterio que Notifications:EmailProvider: con la opción
            // prendida en los user-secrets de quien corre las pruebas y sin bucket público,
            // PaymentProofsOptionsValidator no deja arrancar el host, y todas las pruebas de este
            // proyecto mueren antes de su aserción (spec 2026-09-15, P2). Las pruebas de los
            // comprobantes públicos la prenden con publicPaymentProofLinks, que además fija el bucket
            // público que el validador exige.
            builder.UseSetting(
                "Quotations:PaymentProofs:PublicLinks", _publicPaymentProofLinks ? "true" : "false");
            if (_publicPaymentProofLinks)
            {
                builder.UseSetting("Storage:R2:PublicBucket", "test-public-bucket");
                builder.UseSetting("Storage:R2:PublicBaseUrl", InMemoryPublicObjectStorage.BaseUrl);
            }

            // Fijado, nunca heredado: con un número en los user-secrets de quien corre las pruebas,
            // cada host de este proyecto sembraría la carga de exportación. Las pruebas de la carga lo
            // prenden con WithWebHostBuilder, que se aplica después y gana.
            builder.UseSetting("Seed:ExportLoad:Quotations", "0");

            // Mismo criterio, y por el mismo motivo: `WebApplicationFactory` corre en
            // Development y ahí `CreateBuilder` carga los user-secrets del developer. Si esa
            // persona configuró Zenvia para probar el envío a mano, el registro condicional ve
            // las tres claves, monta `ZenviaWhatsAppSender` y estas pruebas empiezan a mandarle
            // WhatsApps de verdad a clientes de prueba sin teléfono -- que fallan con
            // `quotation.whatsapp.recipient_missing` en aserciones que no tienen nada que ver.
            // Vaciarlas fuerza `LogWhatsAppSender`, que es lo que estas pruebas quieren.
            builder.UseSetting("Quotations:WhatsApp:ApiToken", string.Empty);
            builder.UseSetting("Quotations:WhatsApp:FromNumber", string.Empty);
            builder.UseSetting("Quotations:WhatsApp:TemplateId", string.Empty);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IObjectStorage>();
                services.AddSingleton<IObjectStorage>(ObjectStorage);

                // El publicador real de comprobantes copia al bucket público de R2 por este puerto
                // (spec 2026-09-15); acá las copias quedan en memoria, donde la prueba las ve. El
                // adaptador real llamaría a S3 aunque el bucket no esté configurado.
                services.RemoveAll<IPublicObjectStorage>();
                services.AddSingleton<IPublicObjectStorage>(PublicObjectStorage);

                // El renderer real hace un POST a `qcode-pdf`. Sin sustituirlo, en cuanto el
                // envio genere el PDF estas pruebas saldrian a la red: lentas, dependientes de
                // un servicio ajeno, y consumiendo la cuota de una API key real. Mismo criterio
                // que `IObjectStorage`, que tampoco habla con R2 aca.
                services.RemoveAll<IQuotationPdfRenderer>();
                services.AddSingleton<IQuotationPdfRenderer, StubPdfRenderer>();

                // El adaptador real copia al bucket publico de R2 y falla si no esta
                // configurado -- que es el caso aca, y a proposito: un envio que no puede
                // publicar el PDF no debe darse por bueno. Estas pruebas no ejercitan R2.
                services.RemoveAll<IQuotationPdfStorage>();
                services.AddSingleton<IQuotationPdfStorage, StubPdfStorage>();

                // El worker de exportaciones toma jobs cada 5 s por su cuenta: en una prueba
                // competiría con el tick que la prueba corre a mano (RunExportJobAsync) y la
                // volvería no determinista. Sólo lo deja la prueba que ejerce el worker.
                if (!runExportWorker)
                {
                    var exportWorkers = services
                        .Where(descriptor => descriptor.ServiceType == typeof(IHostedService)
                            && descriptor.ImplementationType == typeof(ExportJobWorker))
                        .ToList();
                    foreach (var descriptor in exportWorkers)
                    {
                        services.Remove(descriptor);
                    }
                }
            });
        }
    }

    public sealed class InMemoryObjectStorage : IObjectStorage
    {
        // Concurrente desde el 2026-09-16: PaymentProofMoveWorker corre en el host cada 3 s y borra
        // temporales mientras la prueba sube y lee (hallazgo 9 del plan).
        private readonly ConcurrentDictionary<string, byte[]> _objects = new(StringComparer.Ordinal);

        public Task<Uri> CreatePresignedUploadUrlAsync(
            string key, string contentType, CancellationToken cancellationToken) =>
            Task.FromResult(new Uri($"https://r2.test/{key}"));

        public Task<Uri> CreatePresignedDownloadUrlAsync(
            string key, string? downloadFileName, CancellationToken cancellationToken) =>
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
            _objects.TryRemove(key, out _);
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

        public bool Exists(string key) => _objects.ContainsKey(key);
    }

    /// <summary>
    /// El bucket público en memoria (spec 2026-09-15). Anota cada copia y cada borrado de los
    /// comprobantes de pago, y puede fallar a propósito en una copia para ejercer el rollback de P7.
    /// <see cref="GetUrl"/> arma la URL con <see cref="BaseUrl"/>, como R2PublicObjectStorage con
    /// Storage:R2:PublicBaseUrl.
    /// </summary>
    public sealed class InMemoryPublicObjectStorage : IPublicObjectStorage
    {
        public const string BaseUrl = "https://assets.qep.test";

        // Concurrente desde D19 (spec 2026-09-16): PaymentProofDetachProcessor borra copias desde el hilo
        // de PaymentProofMoveWorker mientras la prueba lee.
        private readonly ConcurrentDictionary<string, string> _copies = new(StringComparer.Ordinal);
        private int _copyAttempts;

        /// <summary>El intento de copia (desde 1, contando todos los del host) que falla; null si
        /// ninguno.</summary>
        public int? FailingCopyAttempt { get; set; }

        /// <summary>Las copias que siguen en el bucket: clave pública → clave privada de origen.</summary>
        public IReadOnlyDictionary<string, string> Copies => _copies;

        public ConcurrentQueue<string> DeletedKeys { get; } = new();

        /// <summary>Como R2PublicObjectStorage, configurado sólo con bucket público: la factoría lo
        /// prende con <c>publicPaymentProofLinks</c>, que además fija el bucket. Apagado, lo que lo
        /// consulta —la URL pública de las imágenes de producto, por ejemplo— se porta como en CI,
        /// sin bucket público.</summary>
        public bool IsConfigured { get; init; }

        public Task CopyFromPrivateAsync(
            string privateKey, string publicKey, CancellationToken cancellationToken)
        {
            _copyAttempts++;
            if (_copyAttempts == FailingCopyAttempt)
            {
                throw new InvalidOperationException("Simulated failure copying to the public bucket.");
            }

            _copies[publicKey] = privateKey;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string publicKey, CancellationToken cancellationToken)
        {
            _copies.TryRemove(publicKey, out _);
            DeletedKeys.Enqueue(publicKey);
            return Task.CompletedTask;
        }

        public string GetUrl(string publicKey) => $"{BaseUrl}/{publicKey}";

        /// <summary>Las copias vigentes bajo el prefijo, en una sola página. La reconciliación de
        /// Storage no corre en estas pruebas (su intervalo es de horas): existe porque el puerto lo
        /// pide.</summary>
        public Task<PublicObjectPage> ListAsync(
            string prefix, string? continuationToken, CancellationToken cancellationToken) =>
            Task.FromResult(new PublicObjectPage(
                _copies.Keys
                    .Where(key => key.StartsWith(prefix, StringComparison.Ordinal))
                    .Select(key => new PublicStoredObject(key, DateTimeOffset.UtcNow))
                    .ToArray(),
                ContinuationToken: null));
    }
}
