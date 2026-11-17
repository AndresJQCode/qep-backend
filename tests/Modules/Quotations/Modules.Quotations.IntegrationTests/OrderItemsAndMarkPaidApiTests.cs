using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Quotations.Application;
using Modules.Quotations.Infrastructure.Persistence;
using Npgsql;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>Agregar productos a un pedido y marcarlo pagado.</summary>
public sealed class OrderItemsAndMarkPaidApiTests(SharedQuotationsHost sharedHost) : OrderApiTests, IClassFixture<SharedQuotationsHost>
{
    // A pedido (2026-09): "Editar" un pedido pendiente para sumarle productos que faltaron al
    // convertir. Con el pago pendiente (sin comprobantes) agregar un producto no tiene nada que
    // recalcular del lado del pago -- sigue en PaymentPending.
    [Fact]
    public async Task AddOrderItemsAddsAProductAndRecalculatesTheQuotationTotal()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var created = await ReadOrderAsync(await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest(null, []),
            TestContext.Current.CancellationToken));

        var secondProductId = await CreateProductWithScalesAsync(client, tenantId, baseCop: 50_000m);
        var response = await client.PostAsJsonAsync(
            OrderItemsUrl(tenantId, created.Id),
            new AddOrderItemsRequest([new OrderItemAdditionRequest(secondProductId, 1m)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var detail = await response.Content.ReadFromJsonAsync<OrderDetailResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(detail);
        Assert.Equal(2, detail.Items.Count);
        Assert.True(detail.Order.Total > quotation.Total);
        Assert.Contains(detail.Items, item => item.ProductId == secondProductId);
        Assert.Equal("PaymentPending", detail.Order.PaymentStatus);

        var auditMessages = await OutboxMessagesAsync(factory, "platform.audit.recorded.v1");

        // Mismo criterio que las demás acciones quotation.order.*: la entidad auditada es el
        // pedido, así que el id que llega a audit.entries es el del pedido y no el de la cotización.
        var itemAdded = Assert.Single(
            auditMessages, message => ActionOf(message) == "quotation.order.item_added");
        Assert.Equal(detail.Order.Id, Guid.Parse(EntityIdOf(itemAdded)));

        // Guarda del merge con feature/sales, que llegó con los nombres de venta: ninguna acción
        // de auditoría de este flujo puede volver a salir como quotation.sale.*.
        var actions = auditMessages.Select(ActionOf).ToArray();
        Assert.DoesNotContain(actions, action => action.StartsWith("quotation.sale.", StringComparison.Ordinal));
    }

    // Lo cargado en comprobantes no cambia, pero el total contra el que se compara sí: cubría
    // el total viejo por completo y ahora sólo cubre una parte.
    [Fact]
    public async Task AddOrderItemsRecalculatesThePaymentStatusWhenTheTotalGrows()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var proofFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var created = await ReadOrderAsync(await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest(
                null, [new OrderPaymentProofRequest(proofFileId, quotation.Total)]),
            TestContext.Current.CancellationToken));

        var secondProductId = await CreateProductWithScalesAsync(client, tenantId, baseCop: 50_000m);
        var response = await client.PostAsJsonAsync(
            OrderItemsUrl(tenantId, created.Id),
            new AddOrderItemsRequest([new OrderItemAdditionRequest(secondProductId, 1m)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var detail = await response.Content.ReadFromJsonAsync<OrderDetailResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(detail);
        Assert.Equal("PartialPaymentReceived", detail.Order.PaymentStatus);
    }

    // Preparado, el pedido está congelado (spec 2026-11-10 §3.3): agregar un producto ahí
    // adentro cambiaría un paquete que ya se armó.
    [Fact]
    public async Task AddOrderItemsRejectsAPreparedOrder()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ApproverPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var created = await ReadOrderAsync(await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest(null, []),
            TestContext.Current.CancellationToken));
        (await client.PostAsync(
            $"{OrderByIdUrl(tenantId, created.Id)}/prepare",
            null,
            TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        var secondProductId = await CreateProductWithScalesAsync(client, tenantId);
        var response = await client.PostAsJsonAsync(
            OrderItemsUrl(tenantId, created.Id),
            new AddOrderItemsRequest([new OrderItemAdditionRequest(secondProductId, 1m)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("order.order.not_editable", body, StringComparison.Ordinal);
    }

    // Un producto por cotización: agregarlo de nuevo por esta vía es la misma línea con la
    // cantidad partida, mismo invariante que agregarlo desde el editor.
    [Fact]
    public async Task AddOrderItemsRejectsAProductAlreadyInTheQuotation()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var created = await ReadOrderAsync(await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest(null, []),
            TestContext.Current.CancellationToken));

        var response = await client.PostAsJsonAsync(
            OrderItemsUrl(tenantId, created.Id),
            new AddOrderItemsRequest([new OrderItemAdditionRequest(productId, 1m)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("order.item.duplicate_product", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddOrderItemsForAnotherTenantIsForbidden()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, owner) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = owner;
        var clientId = await CreateActiveCustomerAsync(owner, tenantId);
        var productId = await CreateProductWithScalesAsync(owner, tenantId);
        var quotation = await CreateSentQuotationAsync(owner, factory, tenantId, clientId, productId);
        var created = await ReadOrderAsync(await owner.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest(null, []),
            TestContext.Current.CancellationToken));

        var (_, _, otherOwner) = await RegisterTenantAsync(factory, OrdersPermissions.OrderManage);
        using var __ = otherOwner;
        var secondProductId = await CreateProductWithScalesAsync(owner, tenantId);

        var response = await otherOwner.PostAsJsonAsync(
            OrderItemsUrl(tenantId, created.Id),
            new AddOrderItemsRequest([new OrderItemAdditionRequest(secondProductId, 1m)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // Marcar como pagado tiene permiso propio: la asesora registra el pedido con
    // quotations.order.manage, y con eso no puede darse el visto bueno a sí misma.
    [Fact]
    public async Task MarkPaidWithOnlyTheManagePermissionIsForbidden()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var created = await ReadOrderAsync(await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest(null, []),
            TestContext.Current.CancellationToken));

        var response = await client.PostAsync(
            $"{OrderByIdUrl(tenantId, created.Id)}/mark-paid",
            content: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var order = await GetOrderByQuotationAsync(client, tenantId, quotation.Id);
        Assert.NotNull(order);
        Assert.Equal(("Open", "PaymentPending"), (order.Status, order.PaymentStatus));
    }

    // La otra mitad: quien revisa marca pagado con quotations.order.approve aunque no tenga el de
    // gestión. Mismo usuario, otro juego de permisos -- el stub de desarrollo los toma del header.
    [Fact]
    public async Task MarkPaidWithTheApprovePermissionSucceedsWithoutTheManagePermission()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, ownerUserId, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var created = await ReadOrderAsync(await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest(null, []),
            TestContext.Current.CancellationToken));
        using var approver = CreateClient(
            factory, ownerUserId.ToString(), tenantId.ToString(), OrdersPermissions.OrderApprove);

        var approved = await ReadOrderAsync(await approver.PostAsync(
            $"{OrderByIdUrl(tenantId, created.Id)}/mark-paid",
            content: null,
            TestContext.Current.CancellationToken));

        Assert.Equal(("Open", "FullPaymentReceived"), (approved.Status, approved.PaymentStatus));
        Assert.NotNull(approved.PaidMarkedAt);
    }

    // Spec 2026-10-26: con OnOrderApproval, marcar pagado un pedido sin fila de facturación lee el cliente vigente a
    // través del adaptador real (QuotationCustomerLookup): sin tipo de persona es un 422 con missingOn, y al
    // clasificarlo el mismo pedido se marca pagado.
    [Fact]
    public async Task MarkPaidWithOnOrderApprovalIsA422UntilTheCustomerHasAPersonType()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ApproverPermissions);
        using var _ = client;
        const string identificationNumber = "900.123.456-1";
        var clientId = await CreateActiveCustomerAsync(client, tenantId, identificationNumber);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var created = await ReadOrderAsync(await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest(null, []),
            TestContext.Current.CancellationToken));

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var tenancy = scope.ServiceProvider
                .GetRequiredService<Modules.Tenancy.Infrastructure.Persistence.TenancyDbContext>();
            await tenancy.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE tenancy.tenants SET person_type_requirement = 'OnOrderApproval' WHERE id = {tenantId}",
                TestContext.Current.CancellationToken);
        }

        var rejected = await client.PostAsync(
            $"{OrderByIdUrl(tenantId, created.Id)}/mark-paid",
            content: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);
        using var body = JsonDocument.Parse(
            await rejected.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("order.order.billed_person_type_required", body.RootElement.GetProperty("code").GetString());
        Assert.Equal("Customer", body.RootElement.GetProperty("missingOn").GetString());

        await UpdateCustomerRetentionAsync(
            client, tenantId, clientId, identificationNumber,
            withRetention: false, vatSurplus: false, personType: "Natural");

        var approved = await ReadOrderAsync(await client.PostAsync(
            $"{OrderByIdUrl(tenantId, created.Id)}/mark-paid",
            content: null,
            TestContext.Current.CancellationToken));
        Assert.Equal("FullPaymentReceived", approved.PaymentStatus);
    }
}
