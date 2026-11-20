using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Modules.Catalog.Application;
using Modules.Identity.Domain;
using Modules.Identity.Infrastructure.Persistence;
using Modules.Purchases.Application;
using Modules.Purchases.Infrastructure.Messaging;
using Modules.Quotations.Application;
using Modules.Suppliers.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;
using Modules.Tenancy.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace Modules.Purchases.IntegrationTests;

/// <summary>Un tenant sembrado para una prueba: quien llama (<c>SubjectId</c>) y su membresía activa <c>admin</c>.</summary>
internal sealed record PurchasesTestTenant(Guid TenantId, Guid SubjectId, Guid MemberId);

/// <summary>Lo que siembra <see cref="PurchasesApiHarness.NewScenarioAsync"/>. <c>Manager</c> lo libera la prueba.</summary>
internal sealed record PurchasesScenario(
    PurchasesTestTenant Tenant,
    HttpClient Manager,
    Guid SupplierId,
    Guid TaxRateId,
    Guid VariantA,
    Guid VariantB,
    Guid VariantC) : IDisposable
{
    public Guid TenantId => Tenant.TenantId;

    public void Dispose() => Manager.Dispose();
}

/// <summary>
/// El arranque compartido de las pruebas de integración del módulo. Copia el de Loans (base, fábrica y stub de permisos por
/// <c>X-Permissions</c>), con un tenant nuevo por prueba (<see cref="NewTenantAsync"/>) para que varias compartan host
/// (<see cref="SharedPurchasesHost"/>).
/// </summary>
internal static class PurchasesApiHarness
{
    public const string MemberName = "Ana Compras";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static string OrderUrl(Guid tenantId, Guid? orderId = null) =>
        orderId is { } id
            ? $"/api/v1/tenants/{tenantId}/purchases/orders/{id}"
            : $"/api/v1/tenants/{tenantId}/purchases/orders";

    public static async Task<PostgreSqlContainer> StartDatabaseAsync()
    {
        var database = new PostgreSqlBuilder("postgres:18-alpine")
            .WithDatabase("qep")
            .WithUsername("qep")
            .WithPassword("qep-integration")
            .Build();
        await database.StartAsync(Ct);
        return database;
    }

    /// <summary>
    /// Siembra un tenant nuevo con sus módulos (todos por defecto), el usuario que llama y su membresía activa <c>admin</c> con
    /// nombre «Ana Compras»: los handlers de escritura la exigen para el historial.
    /// </summary>
    public static async Task<PurchasesTestTenant> NewTenantAsync(
        QepApiFactory factory, IEnumerable<TenantModuleKey>? modules = null)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var identity = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var tenantId = new Modules.Tenancy.Domain.TenantId(Guid.CreateVersion7());
        var subjectId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;

        tenancy.Tenants.Add(Tenant.Create(
            tenantId,
            // El slug es único en la base: cada tenant lleva el suyo.
            $"purchases-{tenantId.Value:N}",
            "Purchases API Tests",
            "es-CO",
            "America/Bogota",
            "yyyy-MM-dd",
            MembershipId.New(),
            now));
        await tenancy.SaveChangesAsync(Ct);

        foreach (var key in modules ?? TenantModuleKeys.All)
        {
            await tenancy.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO tenancy.tenant_modules (tenant_id, module_key, enabled_at, source)
                VALUES ({tenantId.Value}, {key.Value}, {now}, {TenantModuleSources.Seed})
                ON CONFLICT (tenant_id, module_key) DO NOTHING
                """,
                Ct);
        }

        var user = User.CreateInvited(UserId.Parse(subjectId.ToString()), $"compras-{subjectId:N}@example.com", now);
        identity.Users.Add(user);
        var membership = Membership.CreateActive(MembershipId.New(), user.Id.Value, tenantId, ["admin"], "test", now);
        membership.UpdateProfile(MemberName, null, now);
        tenancy.Memberships.Add(membership);
        await identity.SaveChangesAsync(Ct);
        await tenancy.SaveChangesAsync(Ct);

        return new PurchasesTestTenant(tenantId.Value, subjectId, membership.Id.Value);
    }

    // El stub concede sólo los defaults de tenancy cuando X-Permissions no está, así que cada permiso se pide explícito: cada
    // 403 queda atribuible al permiso o al tenant.
    public static HttpClient CreateClient(QepApiFactory factory, Guid subjectId, Guid tenantId, params string[] permissions)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Subject-Id", subjectId.ToString());
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenantId.ToString());
        if (permissions.Length > 0)
        {
            client.DefaultRequestHeaders.Add("X-Permissions", string.Join(',', permissions));
        }

        return client;
    }

    /// <summary>Gestiona compras y, para sembrar, proveedores y catálogo.</summary>
    public static HttpClient CreateManager(QepApiFactory factory, PurchasesTestTenant tenant) =>
        CreateClient(
            factory,
            tenant.SubjectId,
            tenant.TenantId,
            PurchasesPermissions.OrderRead,
            PurchasesPermissions.OrderManage,
            SuppliersPermissions.Read,
            SuppliersPermissions.Manage,
            CatalogPermissions.ProductRead,
            CatalogPermissions.ProductManage,
            CatalogPermissions.TaxRateManage);

    public static HttpClient CreateReader(QepApiFactory factory, PurchasesTestTenant tenant) =>
        CreateClient(factory, tenant.SubjectId, tenant.TenantId, PurchasesPermissions.OrderRead);

    /// <summary>Un proveedor activo fuera de Colombia (país <c>US</c> con <c>cityName</c>): no necesita ciudad DIVIPOLA.</summary>
    public static async Task<Guid> CreateSupplierAsync(HttpClient client, Guid tenantId, string name, string? email = null)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/suppliers",
            new Dictionary<string, object?>
            {
                ["name"] = name,
                ["businessName"] = null,
                ["identificationType"] = "NIT",
                // El par tipo + número es único por tenant.
                ["identificationNumber"] = $"9{Random.Shared.NextInt64(100_000_000, 999_999_999)}",
                ["country"] = "US",
                ["cityId"] = null,
                ["cityName"] = "Miami",
                ["phone"] = null,
                ["email"] = email,
                ["address"] = null,
                ["paymentTermDays"] = 30,
                ["category"] = null,
                ["notes"] = null,
                ["bankAccounts"] = Array.Empty<object>(),
                ["contacts"] = Array.Empty<object>(),
            },
            Ct);
        await EnsureSuccessAsync(response);
        return await ReadIdAsync(response);
    }

    public static async Task DeactivateSupplierAsync(HttpClient client, Guid tenantId, Guid supplierId)
    {
        var response = await client.PostAsync($"/api/v1/tenants/{tenantId}/suppliers/{supplierId}/deactivate", null, Ct);
        await EnsureSuccessAsync(response);
    }

    public static async Task<Guid> CreateTaxRateAsync(HttpClient client, Guid tenantId, string name, int percentage)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/catalog/tax-rates", new { name, percentage }, Ct);
        await EnsureSuccessAsync(response);
        return await ReadIdAsync(response);
    }

    /// <summary>Un producto con una sola variante (copia de <c>StoreApiHarness.CreateProductAsync</c>); devuelve el id de la variante.</summary>
    public static async Task<Guid> CreateVariantAsync(HttpClient client, Guid tenantId, string code, string name, Guid? taxRateId)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/catalog/products",
            new
            {
                name,
                taxRateId,
                variants = new[]
                {
                    new
                    {
                        code,
                        pricing = new
                        {
                            prices = new Dictionary<string, decimal> { ["COP"] = 10_000m },
                            scales = new object[]
                            {
                                new { fromUnit = 1, toUnit = 999_999, discount = 0m, restriction = "multiple", multiple = 1 },
                            },
                            packagingUnits = Array.Empty<int>(),
                        },
                    },
                },
            },
            Ct);
        await EnsureSuccessAsync(response);
        using var document = JsonDocument.Parse(await BodyAsync(response));
        return document.RootElement.GetProperty("variants")[0].GetProperty("id").GetGuid();
    }

    /// <summary>El <c>If-Match</c> de las escrituras sobre una orden existente, con la forma fuerte <c>"7"</c>.</summary>
    public static HttpRequestMessage WithIfMatch(HttpRequestMessage request, long version)
    {
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
        return request;
    }

    /// <summary>
    /// Un tenant listo para comprar: el gestor, un proveedor activo «Textiles Andinos», una tasa de IVA del 19 % y tres variantes
    /// (A y B con IVA, C sin tasa).
    /// </summary>
    public static async Task<PurchasesScenario> NewScenarioAsync(QepApiFactory factory, string? supplierEmail = null)
    {
        var tenant = await NewTenantAsync(factory);
        var manager = CreateManager(factory, tenant);
        var supplierId = await CreateSupplierAsync(manager, tenant.TenantId, "Textiles Andinos", supplierEmail);
        var taxRateId = await CreateTaxRateAsync(manager, tenant.TenantId, "IVA", 19);
        var variantA = await CreateVariantAsync(manager, tenant.TenantId, "SKU-A", "Camiseta", taxRateId);
        var variantB = await CreateVariantAsync(manager, tenant.TenantId, "SKU-B", "Pantalón", taxRateId);
        var variantC = await CreateVariantAsync(manager, tenant.TenantId, "SKU-C", "Gorra", null);
        return new PurchasesScenario(tenant, manager, supplierId, taxRateId, variantA, variantB, variantC);
    }

    /// <summary>El cuerpo de <c>POST /</c>, <c>PUT /{id}</c> y <c>POST /preview</c>, en COP.</summary>
    public static Dictionary<string, object?> OrderBody(
        Guid supplierId, params (Guid VariantId, decimal Quantity, decimal UnitCost)[] lines) =>
        OrderBodyIn(supplierId, "COP", lines);

    public static Dictionary<string, object?> OrderBodyIn(
        Guid supplierId, string currency, params (Guid VariantId, decimal Quantity, decimal UnitCost)[] lines) =>
        new()
        {
            ["supplierId"] = supplierId,
            ["currency"] = currency,
            ["notes"] = null,
            ["lines"] = lines
                .Select(line => new { variantId = line.VariantId, quantity = line.Quantity, unitCost = line.UnitCost })
                .ToArray(),
        };

    /// <summary>Crea una orden y devuelve su detalle; falla la prueba si el alta no sale bien.</summary>
    public static async Task<PurchaseOrderDetailDto> CreateOrderAsync(
        HttpClient client, Guid tenantId, Dictionary<string, object?> body)
    {
        var response = await client.PostAsJsonAsync(OrderUrl(tenantId), body, Ct);
        await EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<PurchaseOrderDetailDto>(Ct))!;
    }

    public static async Task<PurchaseOrderDetailDto> GetOrderAsync(HttpClient client, Guid tenantId, Guid orderId)
    {
        var response = await client.GetAsync(OrderUrl(tenantId, orderId), Ct);
        await EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<PurchaseOrderDetailDto>(Ct))!;
    }

    /// <summary>Una escritura sobre una orden existente con su <c>If-Match</c>: <c>PUT</c>, <c>DELETE</c> o una transición.</summary>
    public static async Task<HttpResponseMessage> WriteAsync(
        HttpClient client, HttpMethod method, string url, long? version, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (version is { } value)
        {
            WithIfMatch(request, value);
        }

        return await client.SendAsync(request, Ct);
    }

    /// <summary>Una transición sin cuerpo (<c>send</c>, <c>receive</c>, <c>cancel</c>) que tiene que salir bien.</summary>
    public static async Task<PurchaseOrderDetailDto> TransitionAsync(
        HttpClient client, Guid tenantId, PurchaseOrderDetailDto order, string action)
    {
        var response = await WriteAsync(client, HttpMethod.Post, $"{OrderUrl(tenantId, order.Id)}/{action}", order.Version);
        await EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<PurchaseOrderDetailDto>(Ct))!;
    }

    /// <summary>Confirma cada línea tal como se pidió.</summary>
    public static async Task<PurchaseOrderDetailDto> ConfirmAsIsAsync(HttpClient client, Guid tenantId, PurchaseOrderDetailDto order)
    {
        var response = await WriteAsync(
            client,
            HttpMethod.Post,
            $"{OrderUrl(tenantId, order.Id)}/confirm",
            order.Version,
            ConfirmBody(order.Lines.Select(line => (line.Id, line.Quantity!.Value, line.UnitCost!.Value)).ToArray()));
        await EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<PurchaseOrderDetailDto>(Ct))!;
    }

    public static object ConfirmBody(params (Guid LineId, decimal Quantity, decimal UnitCost)[] lines) => new
    {
        lines = lines
            .Select(line => new { lineId = line.LineId, confirmedQuantity = line.Quantity, confirmedUnitCost = line.UnitCost })
            .ToArray(),
    };

    public static async Task<string?> CodeOfAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await BodyAsync(response));
        return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    /// <summary>Las claves de <c>errors</c> de un 422 de validación.</summary>
    public static async Task<IReadOnlyList<string>> ErrorKeysAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await BodyAsync(response));
        return document.RootElement.TryGetProperty("errors", out var errors)
            ? errors.EnumerateObject().Select(property => property.Name).ToList()
            : [];
    }

    /// <summary>Un escalar por SQL directo sobre la base de la prueba (la columna tiene que llamarse <c>"Value"</c>).</summary>
    public static async Task<T> ScalarAsync<T>(QepApiFactory factory, FormattableString sql)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        return await tenancy.Database.SqlQuery<T>(sql).SingleAsync(Ct);
    }

    public static async Task<IReadOnlyList<T>> RowsAsync<T>(QepApiFactory factory, FormattableString sql)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        return await tenancy.Database.SqlQuery<T>(sql).ToListAsync(Ct);
    }

    public static async Task ExecuteAsync(QepApiFactory factory, FormattableString sql)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        await tenancy.Database.ExecuteSqlInterpolatedAsync(sql, Ct);
    }

    public static async Task<string> BodyAsync(HttpResponseMessage response) =>
        await response.Content.ReadAsStringAsync(Ct);

    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"{(int)response.StatusCode} {response.RequestMessage?.RequestUri}: {await BodyAsync(response)}");
        }
    }

    private static async Task<Guid> ReadIdAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await BodyAsync(response));
        return document.RootElement.GetProperty("id").GetGuid();
    }

    /// <param name="configureServices">Sustituciones de servicios para la prueba; corre después del registro de la aplicación.</param>
    public sealed class QepApiFactory(string connectionString, Action<IServiceCollection>? configureServices = null)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:QepDatabase", connectionString);
            builder.UseSetting("OpenTelemetry:Endpoint", string.Empty);
            builder.UseSetting("Storage:R2:AccountId", "test-account");
            builder.UseSetting("Storage:R2:AccessKeyId", "test-access-key");
            builder.UseSetting("Storage:R2:SecretAccessKey", "test-secret");
            builder.UseSetting("Storage:R2:Bucket", "test-bucket");
            // Fijado, nunca heredado de appsettings.json: con "infobip" y las claves ausentes el validador de Notifications falla
            // al arrancar.
            builder.UseSetting("Notifications:EmailProvider", "log");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:DryRun", "true");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:MinimumAgeHours", "24");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:IntervalHours", "24");
            builder.UseSetting("Quotations:PaymentProofs:PublicLinks", "false");
            // Fijado: los user-secrets del developer pueden traer Seed:Enabled=true y sembrar datos en la base de pruebas.
            builder.UseSetting("Seed:Enabled", "false");

            // El PDF nunca sale a qcode-pdf en las pruebas: el falso devuelve unos bytes fijos y guarda el último documento.
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IPurchaseOrderPdfRenderer>();
                services.AddSingleton<FakePurchaseOrderPdfRenderer>();
                services.AddSingleton<IPurchaseOrderPdfRenderer>(provider =>
                    provider.GetRequiredService<FakePurchaseOrderPdfRenderer>());

                // El PDF del pedido del mayorista (su propio documento) tampoco sale a la red: se guarda por número.
                services.RemoveAll<IQuotationPdfRenderer>();
                services.AddSingleton<FakeQuotationPdfRenderer>();
                services.AddSingleton<IQuotationPdfRenderer>(provider => provider.GetRequiredService<FakeQuotationPdfRenderer>());
                services.RemoveAll<IQuotationPdfStorage>();
                services.AddSingleton<IQuotationPdfStorage, FakeQuotationPdfStorage>();

                // El correo al proveedor nunca sale a un proveedor real: el falso guarda lo que recibe (las pruebas lo filtran por
                // la dirección, única por prueba) y falla con las direcciones que empiezan por «fail-».
                services.RemoveAll<IPurchaseOrderEmail>();
                services.AddSingleton<FakePurchaseOrderEmail>();
                services.AddSingleton<IPurchaseOrderEmail>(provider => provider.GetRequiredService<FakePurchaseOrderEmail>());

                // Mayorista fase 3 (revisión del bloque C, I1): el worker de los eventos del mayorista sondea solo y competiría
                // con LinkedPurchasesHarness.ProcessEventsAsync, volviendo no deterministas las pruebas de eventos. Se saca el
                // hosted service como en PipelineApiHarness; el procesador (singleton) sigue registrado. Es el único hosted
                // service que consume quotations.order.wholesale-*; que esté registrado por tipo (y este filtro lo encuentre) lo
                // amarra WholesaleOrderEventsProcessorTests.
                var workers = services
                    .Where(descriptor => descriptor.ServiceType == typeof(IHostedService)
                        && descriptor.ImplementationType == typeof(WholesaleOrderEventsWorker))
                    .ToList();
                foreach (var descriptor in workers)
                {
                    services.Remove(descriptor);
                }
            });

            if (configureServices is not null)
            {
                builder.ConfigureServices(configureServices);
            }
        }
    }
}
