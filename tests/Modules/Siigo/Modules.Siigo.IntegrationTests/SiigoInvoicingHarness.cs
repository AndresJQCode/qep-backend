using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Modules.Customers.Application;
using Modules.Quotations.Application;
using Modules.Siigo.Application;
using Modules.Siigo.Application.Invoicing;
using Modules.Siigo.Domain;
using Modules.Siigo.Infrastructure.Batches;
using Modules.Tenancy.Application;
using static Modules.Siigo.IntegrationTests.SiigoApiHarness;

namespace Modules.Siigo.IntegrationTests;

/// <summary>
/// Lo que necesitan las pruebas de facturación (spec 2026-11-18 §5.4): un tenant con Siigo prendido, una conexión configurada de punta a
/// punta (empresa, comprobante, impuestos, formas de pago y vínculo del producto) y un pedido pagado de verdad, creado por las APIs de
/// Customers, Catalog, Companies y Quotations. La API de Siigo es el doble <see cref="FakeSiigoApi"/>.
/// </summary>
internal static class SiigoInvoicingHarness
{
    public const string SiigoCode = "SKU-100";

    public static readonly string[] InvoicerPermissions =
    [
        SiigoPermissions.InvoiceIssue,
        SiigoPermissions.SettingsManage,
        OrdersPermissions.OrderRead,
        OrdersPermissions.OrderManage,
        OrdersPermissions.OrderApprove,
        OrdersPermissions.OrderInvoice,
        CustomersPermissions.CustomerRead,
        CustomersPermissions.CustomerManage,
        CustomersPermissions.ClassificationRead,
        CustomersPermissions.ClassificationManage,
        .. SeedPermissions,
    ];

    internal sealed record InvoicingWorld(
        WebApplicationFactory<Program> Factory,
        FakeSiigoApi Siigo,
        RegisteredTenant Tenant,
        HttpClient Client,
        string ConnectionString,
        Guid ConnectionId,
        Guid OrderId,
        string OrderNumber,
        Guid VariantId,
        Guid CustomerId,
        CompanyRef Company) : IDisposable
    {
        public Guid TenantId => Tenant.TenantId;

        public string BaseRoute => $"/api/v1/tenants/{TenantId}/orders/{OrderId}/siigo-invoice";

        public void Dispose()
        {
            Client.Dispose();
            Factory.Dispose();
        }
    }

    /// <summary>Un cliente con la identificación que Siigo conoce (NIT) y una ciudad con código DANE, por la API de Customers.</summary>
    public static async Task<Guid> CreateCustomerAsync(HttpClient client, Guid tenantId, string name = "Verde Esencial S.A.S.")
    {
        var cityId = await AnyCityIdAsync(client);
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var classification = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/customers/classifications",
            new { name = $"Mediano-{suffix}", prefix = $"C{suffix}" },
            TestContext.Current.CancellationToken);
        await EnsureSuccessAsync(classification);
        var classificationId = (await classification.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("id").GetGuid();

        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/customers",
            new
            {
                name,
                identificationType = "NIT",
                identificationNumber = $"900.{Random.Shared.Next(100, 999)}.{Random.Shared.Next(100, 999)}-1",
                phone = "310 935 2187",
                email = "compras@verde.co",
                address = "Calle 10 # 45-12",
                country = "CO",
                personType = "Juridica",
                cityId,
                classificationId,
                withRetention = false,
                vatSurplus = false,
                withholdingTaxRateIds = Array.Empty<Guid>(),
            },
            TestContext.Current.CancellationToken);
        await EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("id").GetGuid();
    }

    /// <summary>
    /// El mundo completo: tenant, módulo prendido, conexión, empresa con cuenta, IVA al 19 %, un producto vinculado a Siigo, un cliente y un
    /// pedido manual de 3 unidades marcado como pagado. Con <paramref name="freight"/> el pedido lleva envío cobrado. Las respuestas de
    /// Siigo (cliente que no existe, factura creada) son las felices; cada prueba las cambia.
    /// </summary>
    public static async Task<InvoicingWorld> ArrangeAsync(
        TestDatabase database,
        bool sendToDian = false,
        bool freightSeparate = false,
        bool freight = false,
        bool linkProduct = true,
        bool markPaid = true,
        bool advisorLinked = true)
    {
        var connectionString = database.GetConnectionString();
        var siigo = new FakeSiigoApi();
        var factory = new QepApiFactory(connectionString, siigo);
        var tenant = await RegisterTenantAsync(factory);
        await EnableSiigoAsync(connectionString, tenant.TenantId);
        var connectionId = await LinkSiigoConnectionAsync(factory, tenant.TenantId, "Siigo principal");
        var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, InvoicerPermissions);
        var ct = TestContext.Current.CancellationToken;

        var company = await CreateCompanyAsync(client, tenant.TenantId);
        var vat = await CreateTaxRateAsync(client, tenant.TenantId, "IVA 19%", 19m);
        var variantId = await CreateProductAsync(client, tenant.TenantId, SiigoCode, "Aceite esencial", 11_900m, vat);
        var customerId = await CreateCustomerAsync(client, tenant.TenantId);

        var created = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenant.TenantId}/orders",
            new
            {
                clientId = customerId,
                paymentMethod = "Transferencia",
                notes = "Pedido de prueba",
                isRetail = false,
                billingAccount = new { companyId = company.Id, bankName = company.BankName, accountNumber = company.AccountNumber, currency = "COP" },
                items = new[] { new { variantId, quantity = 3m } },
                paymentProofs = Array.Empty<object>(),
                shipping = freight ? new { carrierId = (Guid?)null, freightChargeMode = "Charged", freightAmount = 11_900m, cashOnDelivery = false } : null,
            },
            ct);
        await EnsureSuccessAsync(created);
        var order = (await created.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("order");
        var orderId = order.GetProperty("id").GetGuid();
        var orderNumber = order.GetProperty("orderNumber").GetString()!;
        if (markPaid)
        {
            await EnsureSuccessAsync(await client.PostAsync($"/api/v1/tenants/{tenant.TenantId}/orders/{orderId}/mark-paid", null, ct));
        }

        siigo.AddProduct("p-100", SiigoCode, "Aceite esencial Siigo");
        await ConfigureSettingsAsync(factory, tenant, connectionId, company, variantId, sendToDian, freightSeparate, linkProduct, advisorLinked);

        // Lo feliz de Siigo: el cliente no existe y la factura se crea.
        siigo.OnRequest("/v1/customers", call => call.Method == HttpMethod.Get
            ? FakeSiigoApi.Respond(System.Net.HttpStatusCode.OK, """{"pagination":{"page":1,"page_size":25,"total_results":0},"results":[]}""")
            : FakeSiigoApi.Respond(System.Net.HttpStatusCode.Created, """{"id":"c-1","identification":"900123456","active":true}"""));
        siigo.OnRequest("/v1/invoices", _ => Created("FV-1-123", "d1b6c0a2-77aa-4e5f-9a0c-0a1b2c3d4e5f", 0m));
        return new InvoicingWorld(factory, siigo, tenant, client, connectionString, connectionId, orderId, orderNumber, variantId, customerId, company);
    }

    public static HttpResponseMessage Created(string number, string id, decimal total, string? cufe = null)
    {
        var amount = total.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var stamp = cufe is null ? string.Empty : ",\"stamp\":{\"status\":\"Accepted\",\"cufe\":\"" + cufe + "\"}";
        return FakeSiigoApi.Respond(
            System.Net.HttpStatusCode.Created,
            "{\"id\":\"" + id + "\",\"name\":\"" + number + "\",\"total\":" + amount + stamp + "}");
    }

    // La configuración que haría el asistente, por el repositorio y el dominio (los endpoints del asistente ya tienen sus pruebas).
    private static async Task ConfigureSettingsAsync(
        WebApplicationFactory<Program> factory,
        RegisteredTenant tenant,
        Guid connectionId,
        CompanyRef company,
        Guid variantId,
        bool sendToDian,
        bool freightSeparate,
        bool linkProduct,
        bool linkAdvisor)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<ISiigoSettingsRepository>();
        var settings = await repository.FindOrCreateAsync(tenant.TenantId, connectionId, withVariantLinks: true, TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException("The settings of the connection were not created.");
        settings.AssignCompany(company.Id);
        settings.UpdateDocument(new SiigoDocumentSettings("24446", "11", "3", sendToDian, false, freightSeparate, "FLETE"));
        settings.ReplaceTaxLinks([new SiigoTaxLink(19, "13156")]);
        settings.UpdatePayments(new SiigoPaymentSettings(
            [new SiigoPaymentLink(BankAccountKey.Normalize(company.BankName, company.AccountNumber)!, "5101")],
            CreditPaymentTypeId: "5102",
            CreditDays: 30,
            CashOnDeliveryPaymentTypeId: "5103",
            DefaultPaymentTypeId: "5101"));
        // El vendedor es el usuario de Siigo vinculado al asesor del pedido, que es el owner que lo creó (spec 2026-11-20).
        if (linkAdvisor)
        {
            var owner = await scope.ServiceProvider.GetRequiredService<IMembershipDirectory>()
                .FindActiveMembershipIdAsync(tenant.OwnerUserId, tenant.TenantId, TestContext.Current.CancellationToken)
                ?? throw new InvalidOperationException("The owner has no active membership.");
            settings.ReplaceSellerLinks([new SiigoSellerLink(owner, "629", "ANA GOMEZ")]);
        }

        if (linkProduct)
        {
            settings.LinkVariant(
                variantId, SiigoCode, "p-100", "Aceite esencial Siigo", SiigoLinkOrigin.Manual, tenant.OwnerUserId, DateTimeOffset.UtcNow);
        }

        await scope.ServiceProvider.GetRequiredService<ISiigoUnitOfWork>().SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    public static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        await EnsureSuccessAsync(response);
        return await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
    }

    /// <summary>El pedido tal como la API de pedidos lo entrega.</summary>
    public static async Task<JsonElement> GetOrderAsync(InvoicingWorld world) =>
        (await ReadAsync(await world.Client.GetAsync($"/api/v1/tenants/{world.TenantId}/orders/{world.OrderId}", TestContext.Current.CancellationToken)))
        .GetProperty("order");

    /// <summary>
    /// Factura el pedido del mundo como lo hace la pantalla (spec 2026-11-24, etapa contract): una tanda de ese pedido y una pasada del
    /// worker. Devuelve el <c>GET …/siigo-invoice</c> del pedido, o el ítem de la tanda cuando salió <c>Skipped</c> o <c>Failed</c> (ahí
    /// puede no haber fila). <paramref name="payments"/> son las formas de pago elegidas.
    /// </summary>
    public static async Task<JsonElement> IssueThroughBatchAsync(InvoicingWorld world, object? payments = null)
    {
        var item = await RunBatchOfOneAsync(world, payments);
        return item.GetProperty("status").GetString() is "Skipped" or "Failed"
            ? item
            : await ReadAsync(await world.Client.GetAsync(world.BaseRoute, TestContext.Current.CancellationToken));
    }

    /// <summary><see cref="IssueThroughBatchAsync"/> para el pedido que tiene que quedar facturado o por confirmar: las filas de sus
    /// facturas. Falla si la tanda lo saltó o lo dio por fallido.</summary>
    public static async Task<JsonElement> IssueRowsThroughBatchAsync(InvoicingWorld world)
    {
        var result = await IssueThroughBatchAsync(world);
        Assert.True(result.ValueKind == JsonValueKind.Array, result.ToString());
        return result;
    }

    /// <summary>Una tanda del pedido del mundo y una pasada del worker de ese host: el ítem tal como la pantalla de la tanda lo lee.</summary>
    public static async Task<JsonElement> RunBatchOfOneAsync(InvoicingWorld world, object? payments = null)
    {
        var ct = TestContext.Current.CancellationToken;
        var route = $"/api/v1/tenants/{world.TenantId}/siigo/invoice-batches";
        var created = await ReadAsync(await world.Client.PostAsJsonAsync(route, new { orderIds = new[] { world.OrderId }, payments }, ct));
        await world.Factory.Services.GetServices<IHostedService>().OfType<SiigoInvoiceBatchWorker>().Single().DrainAsync(ct);
        var batch = await ReadAsync(await world.Client.GetAsync($"{route}/{created.GetProperty("id").GetGuid()}", ct));
        // Una excepción que el ejecutor no traduce la traga el worker y deja la tanda abierta: sin esto la prueba seguiría como si nada.
        Assert.Equal("Finished", batch.GetProperty("status").GetString());
        return batch.GetProperty("items")[0];
    }

    /// <summary>
    /// La emisión que el worker corre por cada pedido, sin crear una tanda. Para dejar una factura antes de preparar un ítem cortado, y
    /// para competir dos emisiones del mismo pedido (dos tandas abiertas de un pedido no pueden existir).
    /// </summary>
    public static async Task<SiigoBatchItemOutcome> IssueWithoutBatchAsync(InvoicingWorld world)
    {
        await using var scope = world.Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISiigoBatchItemInvoker>()
            .InvokeAsync(world.TenantId, world.Tenant.OwnerUserId, world.OrderId, null, TestContext.Current.CancellationToken);
    }

    /// <summary>El cuerpo de cada <c>POST /v1/invoices</c> que recibió el doble, en orden.</summary>
    public static IReadOnlyList<FakeSiigoCall> InvoiceCalls(FakeSiigoApi siigo) =>
        [.. siigo.Calls.Where(call => call.Path == "/v1/invoices" && call.Method == HttpMethod.Post)];
}
