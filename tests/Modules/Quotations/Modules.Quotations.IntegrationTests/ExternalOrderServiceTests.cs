using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>Spec 2026-10-15 §5.4: el puerto con el que una integración (Shopify) crea pedidos sin sesión HTTP.
/// Las líneas llegan preciadas y el cliente puede estar incompleto; el estado de pago que avisa la plataforma
/// sólo manda mientras el pedido no tenga comprobantes propios.</summary>
public sealed class ExternalOrderServiceTests
{
    private static string OrdersUrl(Guid tenantId) => $"/api/v1/tenants/{tenantId}/orders";

    [Fact]
    public async Task CreatesAnOrderForAnIncompleteCustomerAndPublishesOrderCreated()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, ownerUserId, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var incompleteId = await CreateActiveCustomerAsync(client, tenantId);
        await IncompleteCustomerQuotationApiTests.MarkIncompleteAsync(database.GetConnectionString(), incompleteId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var billing = await CreateCompanyWithBankAccountAsync(client, tenantId);
        var advisorId = await OwnerMembershipIdAsync(factory, tenantId, ownerUserId);

        ExternalOrderResult result;
        using (var scope = factory.Services.CreateScope())
        {
            var writer = scope.ServiceProvider.GetRequiredService<IExternalOrderWriter>();
            result = await writer.CreateAsync(
                new ExternalOrderDraft(
                    tenantId, OrderSource.Shopify, "#1001", incompleteId, advisorId, "shopify_payments",
                    "Shopify #1001", null,
                    new QuotationBillingAccountRequest(billing.CompanyId, billing.BankName, billing.AccountNumber, billing.Currency),
                    [new ExternalOrderLine(productId, productId, 2m, 11_900m, 0m, 19)],
                    OrderPaymentStatus.FullPaymentReceived,
                    DateTimeOffset.UtcNow),
                TestContext.Current.CancellationToken);
        }

        Assert.StartsWith("PED-", result.OrderNumber, StringComparison.Ordinal);
        Assert.Equal(23_800m, result.Total);
        Assert.Equal("COP", result.Currency);
        var detail = await client.GetFromJsonAsync<OrderDetailResponse>(
            $"{OrdersUrl(tenantId)}/{result.OrderId}", TestContext.Current.CancellationToken);
        Assert.NotNull(detail);
        Assert.Equal(
            ("Shopify", "#1001", "FullPaymentReceived", "Pending"),
            (detail.Order.Source, detail.Order.ExternalReference, detail.Order.PaymentStatus, detail.Order.Status));
        Assert.Equal(incompleteId, detail.Order.ClientId);
        Assert.Equal(advisorId, detail.Order.AdvisorId);
        Assert.Equal("Created", Assert.Single(detail.History).EventType);
        var created = Assert.Single(await OutboxMessagesAsync(factory, "quotations.order.created.v1"));
        using var payload = JsonDocument.Parse(created.PayloadJson);
        Assert.Equal("Shopify", payload.RootElement.GetProperty("source").GetString());
        // La auditoría la firma la integración, no una persona.
        var audit = Assert.Single(
            await OutboxMessagesAsync(factory, "platform.audit.recorded.v1"),
            message => message.PayloadJson.Contains("quotation.order.created", StringComparison.Ordinal));
        using var auditPayload = JsonDocument.Parse(audit.PayloadJson);
        Assert.Equal("Integration", auditPayload.RootElement.GetProperty("actorType").GetString());
    }

    [Fact]
    public async Task SyncPaymentStatusChangesAnOrderWithoutProofsAndIsRejectedWithThem()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, ownerUserId, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var billing = await CreateCompanyWithBankAccountAsync(client, tenantId);
        var advisorId = await OwnerMembershipIdAsync(factory, tenantId, ownerUserId);
        // Con un comprobante: el estado lo gobiernan los comprobantes.
        var withProofNumber = await OrderApiTests.ConvertOneAsync(client, factory, tenantId, clientId, productId);
        var page = await client.GetFromJsonAsync<OrdersPageResponse>(
            $"{OrdersUrl(tenantId)}?orderNumber={withProofNumber}", TestContext.Current.CancellationToken);
        var withProofId = Assert.Single(page!.Items).Id;

        using var scope = factory.Services.CreateScope();
        var writer = scope.ServiceProvider.GetRequiredService<IExternalOrderWriter>();
        var external = await writer.CreateAsync(
            new ExternalOrderDraft(
                tenantId, OrderSource.Shopify, "#1002", clientId, advisorId, null, null, null,
                new QuotationBillingAccountRequest(billing.CompanyId, billing.BankName, billing.AccountNumber, billing.Currency),
                [new ExternalOrderLine(productId, productId, 1m, 11_900m, 0m, 19)],
                OrderPaymentStatus.PaymentPending,
                DateTimeOffset.UtcNow),
            TestContext.Current.CancellationToken);

        await writer.SyncPaymentStatusAsync(
            tenantId, external.OrderId, OrderPaymentStatus.FullPaymentReceived, TestContext.Current.CancellationToken);
        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            writer.SyncPaymentStatusAsync(
                tenantId, withProofId, OrderPaymentStatus.PaymentPending, TestContext.Current.CancellationToken));

        Assert.Equal("order.order.payment_has_proofs", error.Code);
        var synced = await client.GetFromJsonAsync<OrderDetailResponse>(
            $"{OrdersUrl(tenantId)}/{external.OrderId}", TestContext.Current.CancellationToken);
        Assert.Equal("FullPaymentReceived", synced!.Order.PaymentStatus);
        var untouched = await client.GetFromJsonAsync<OrderDetailResponse>(
            $"{OrdersUrl(tenantId)}/{withProofId}", TestContext.Current.CancellationToken);
        Assert.Equal("FullPaymentReceived", untouched!.Order.PaymentStatus);
    }

    [Fact]
    public async Task CancelLeavesTheOrderCancelledWithItsHistoryAndEvent()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, ownerUserId, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var billing = await CreateCompanyWithBankAccountAsync(client, tenantId);
        var advisorId = await OwnerMembershipIdAsync(factory, tenantId, ownerUserId);

        using var scope = factory.Services.CreateScope();
        var writer = scope.ServiceProvider.GetRequiredService<IExternalOrderWriter>();
        var external = await writer.CreateAsync(
            new ExternalOrderDraft(
                tenantId, OrderSource.Shopify, "#1003", clientId, advisorId, null, null, null,
                new QuotationBillingAccountRequest(billing.CompanyId, billing.BankName, billing.AccountNumber, billing.Currency),
                [new ExternalOrderLine(productId, productId, 1m, 11_900m, 0m, 19)],
                OrderPaymentStatus.PaymentPending,
                DateTimeOffset.UtcNow),
            TestContext.Current.CancellationToken);

        await writer.CancelAsync(
            tenantId, external.OrderId, advisorId, "Cancelado en Shopify", TestContext.Current.CancellationToken);

        var detail = await client.GetFromJsonAsync<OrderDetailResponse>(
            $"{OrdersUrl(tenantId)}/{external.OrderId}", TestContext.Current.CancellationToken);
        Assert.Equal(("Cancelled", "Cancelado en Shopify"), (detail!.Order.Status, detail.Order.CancellationReason));
        Assert.Contains(detail.History, entry => entry.EventType == "Cancelled");
        Assert.Single(await OutboxMessagesAsync(factory, "quotations.order.cancelled.v1"));
    }

    // Spec de Shopify §6.2 y flete §10: el envío que cobró la tienda entra con el pedido y suma al total.
    [Fact]
    public async Task CreateAsyncWithShippingPersistsShippingAndTotals()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var setup = await ArrangeAsync(factory);
        using var _ = setup.Client;
        var shipping = new ShippingDetails(null, null, FreightChargeMode.Charged, 15_000m, 19, false);

        var result = await CreateAsync(factory, Draft(setup, "#2001", shipping: shipping));

        Assert.False(result.AlreadyExisted);
        Assert.Equal(23_800m + 15_000m, result.Total);
        var detail = await setup.Client.GetFromJsonAsync<OrderDetailResponse>(
            $"{OrdersUrl(setup.TenantId)}/{result.OrderId}", TestContext.Current.CancellationToken);
        Assert.NotNull(detail);
        Assert.Equal(
            ("Charged", 15_000m, 19, false),
            (detail.Order.Shipping.ChargeMode, detail.Order.Shipping.FreightAmount,
                detail.Order.Shipping.FreightTaxPercentage, detail.Order.Shipping.CashOnDelivery));
        Assert.Equal((12_605.04m, 2_394.96m), (detail.Order.FreightSubtotal, detail.Order.FreightTaxAmount));
        Assert.Equal(38_800m, detail.Order.Total);
    }

    // D-S23: el webhook y la conciliación, o un reintento tras una caída del worker, mandan el mismo pedido dos
    // veces. La segunda llamada devuelve el que ya existe y no escribe nada: ni otro evento ni otro historial.
    [Fact]
    public async Task CreateAsyncWithSameExternalKeyReturnsExistingOrder()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var setup = await ArrangeAsync(factory);
        using var _ = setup.Client;

        var first = await CreateAsync(factory, Draft(setup, "#2002", externalKey: "shop-1:order-2002"));
        var second = await CreateAsync(factory, Draft(setup, "#2002", externalKey: " shop-1:order-2002 "));

        Assert.False(first.AlreadyExisted);
        Assert.True(second.AlreadyExisted);
        Assert.Equal(
            (first.OrderId, first.OrderNumber, first.Total, first.NetTotal, first.Currency, first.ClientId),
            (second.OrderId, second.OrderNumber, second.Total, second.NetTotal, second.Currency, second.ClientId));
        // El cliente del pedido como quedó guardado: Shopify lo anota en su espejo aunque el reintento haya resuelto otro.
        Assert.Equal(setup.ClientId, first.ClientId);
        Assert.Single(await OutboxMessagesAsync(factory, "quotations.order.created.v1"));
        var detail = await setup.Client.GetFromJsonAsync<OrderDetailResponse>(
            $"{OrdersUrl(setup.TenantId)}/{first.OrderId}", TestContext.Current.CancellationToken);
        Assert.Single(detail!.History);
        Assert.Equal(1, await CountShopifyOrdersAsync(setup));
    }

    // La carrera que la búsqueda previa no cierra: las dos llamadas buscan antes de que alguna guarde. La cierra
    // el índice IX_orders_tenant_external_key, y la que pierde devuelve el pedido de la que ganó.
    [Fact]
    public async Task CreateAsyncConcurrentSameExternalKeyCreatesOneOrder()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var setup = await ArrangeAsync(factory);
        using var _ = setup.Client;
        var draft = Draft(setup, "#2003", externalKey: "shop-1:order-2003");

        var results = await Task.WhenAll(CreateAsync(factory, draft), CreateAsync(factory, draft));

        Assert.Equal(results[0].OrderId, results[1].OrderId);
        Assert.Single(results, result => result.AlreadyExisted);
        Assert.Single(await OutboxMessagesAsync(factory, "quotations.order.created.v1"));
        Assert.Equal(1, await CountShopifyOrdersAsync(setup));
    }

    // La carrera de la clave externa, determinista: la búsqueda previa no ve el pedido que ya existe (como si la otra llamada hubiera
    // guardado justo después), así que el alta choca con IX_orders_tenant_external_key. El servicio descarta lo pendiente, relee
    // y devuelve el ganador, y la transacción deshecha devuelve el consecutivo: el próximo pedido no deja hueco.
    [Fact]
    public async Task CreateAsyncLosingTheIndexRaceDiscardsAndReturnsTheWinnerWithoutSkippingTheNumber()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var setup = await ArrangeAsync(factory);
        using var _ = setup.Client;
        var draft = Draft(setup, "#2007", externalKey: "shop-1:order-2007");
        var winner = await CreateAsync(factory, draft);

        ExternalOrderResult loser;
        BlindOnceOrderRepository blind;
        CountingUnitOfWork unitOfWork;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            blind = BlindOnceOrderRepository.Wrap(scope.ServiceProvider.GetRequiredService<IOrderRepository>());
            unitOfWork = new CountingUnitOfWork(scope.ServiceProvider.GetRequiredService<IQuotationsUnitOfWork>());
            var service = ActivatorUtilities.CreateInstance<ExternalOrderService>(scope.ServiceProvider, (IOrderRepository)blind, (IQuotationsUnitOfWork)unitOfWork);
            loser = await service.CreateAsync(draft, TestContext.Current.CancellationToken);
        }

        var next = await CreateAsync(factory, Draft(setup, "#2008", externalKey: "shop-1:order-2008"));

        Assert.True(loser.AlreadyExisted);
        Assert.Equal((winner.OrderId, winner.OrderNumber, winner.ClientId), (loser.OrderId, loser.OrderNumber, loser.ClientId));
        Assert.Equal((1, 2), (unitOfWork.Discards, blind.ExternalKeyLookups));
        Assert.Equal(Sequence(winner.OrderNumber) + 1, Sequence(next.OrderNumber));
        Assert.Equal(2, (await OutboxMessagesAsync(factory, "quotations.order.created.v1")).Count);
        Assert.Equal(2, await CountShopifyOrdersAsync(setup));
    }

    // La clave es por tenant (D-S23, índice (tenant_id, source, external_key)): la misma tienda enlazada a dos tenants crea un
    // pedido en cada uno.
    [Fact]
    public async Task CreateAsyncSameExternalKeyInAnotherTenantCreatesItsOwnOrder()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var setup = await ArrangeAsync(factory);
        using var _ = setup.Client;
        var other = await ArrangeAsync(factory);
        using var __ = other.Client;

        var first = await CreateAsync(factory, Draft(setup, "#2009", externalKey: "shop-1:order-2009"));
        var second = await CreateAsync(factory, Draft(other, "#2009", externalKey: "shop-1:order-2009"));

        Assert.Equal((false, false), (first.AlreadyExisted, second.AlreadyExisted));
        Assert.NotEqual(first.OrderId, second.OrderId);
        Assert.Equal((1, 1), (await CountShopifyOrdersAsync(setup), await CountShopifyOrdersAsync(other)));
    }

    private static int Sequence(string orderNumber) =>
        int.Parse(new string(orderNumber.Reverse().TakeWhile(char.IsAsciiDigit).Reverse().ToArray()), System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Delega todo en el repositorio real, salvo la primera búsqueda por clave externa, que no encuentra nada: la
    /// carrera que la búsqueda previa no puede cerrar, sin depender del reloj.</summary>
    public class BlindOnceOrderRepository : System.Reflection.DispatchProxy
    {
        private IOrderRepository inner = null!;

        public int ExternalKeyLookups { get; private set; }

        public static BlindOnceOrderRepository Wrap(IOrderRepository inner)
        {
            var proxy = Create<IOrderRepository, BlindOnceOrderRepository>();
            var blind = (BlindOnceOrderRepository)(object)proxy;
            blind.inner = inner;
            return blind;
        }

        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            if (targetMethod.Name == nameof(IOrderRepository.FindByExternalKeyAsync) && ++ExternalKeyLookups == 1)
            {
                return Task.FromResult<Order?>(null);
            }

            return targetMethod.Invoke(inner, args);
        }
    }

    private sealed class CountingUnitOfWork(IQuotationsUnitOfWork inner) : IQuotationsUnitOfWork
    {
        public int Discards { get; private set; }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken) => inner.SaveChangesAsync(cancellationToken);

        public Task<IQuotationsTransaction> BeginTransactionAsync(CancellationToken cancellationToken) =>
            inner.BeginTransactionAsync(cancellationToken);

        public void DiscardPendingChanges()
        {
            Discards++;
            inner.DiscardPendingChanges();
        }
    }

    // Spec de Shopify §6.3: antes de ignorar una fila en espera que se canceló, el espejo pregunta si el pedido ya existe con su
    // clave. Sólo lee, y sólo dentro del tenant y del origen: la misma clave en otro tenant no es este pedido.
    [Fact]
    public async Task FindByExternalKeyAsyncReturnsTheOrderOnlyInItsTenantAndSource()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var setup = await ArrangeAsync(factory);
        using var _ = setup.Client;
        var other = await ArrangeAsync(factory);
        using var __ = other.Client;
        var created = await CreateAsync(factory, Draft(setup, "#2006", externalKey: "shop-1:order-2006"));

        await using var scope = factory.Services.CreateAsyncScope();
        var writer = scope.ServiceProvider.GetRequiredService<IExternalOrderWriter>();
        var ct = TestContext.Current.CancellationToken;
        var found = await writer.FindByExternalKeyAsync(setup.TenantId, OrderSource.Shopify, " shop-1:order-2006 ", ct);
        var otherTenant = await writer.FindByExternalKeyAsync(other.TenantId, OrderSource.Shopify, "shop-1:order-2006", ct);
        var otherSource = await writer.FindByExternalKeyAsync(setup.TenantId, OrderSource.Manual, "shop-1:order-2006", ct);

        Assert.NotNull(found);
        Assert.Equal(
            (created.OrderId, created.OrderNumber, created.Total, created.ClientId, true),
            (found.OrderId, found.OrderNumber, found.Total, found.ClientId, found.AlreadyExisted));
        Assert.Null(otherTenant);
        Assert.Null(otherSource);
    }

    // §6.2.5: la pantalla ofrece «Reconocer este nombre» sin leer el espejo de Shopify.
    [Fact]
    public async Task GetOrderExposesExternalShippingTitle()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var setup = await ArrangeAsync(factory);
        using var _ = setup.Client;

        var withTitle = await CreateAsync(factory, Draft(setup, "#2004", externalShippingTitle: "Envío exprés"));
        var withoutTitle = await CreateAsync(factory, Draft(setup, "#2005"));

        var detail = await setup.Client.GetFromJsonAsync<OrderDetailResponse>(
            $"{OrdersUrl(setup.TenantId)}/{withTitle.OrderId}", TestContext.Current.CancellationToken);
        Assert.Equal("Envío exprés", detail!.Order.Shipping.ExternalShippingTitle);
        var plain = await setup.Client.GetFromJsonAsync<OrderDetailResponse>(
            $"{OrdersUrl(setup.TenantId)}/{withoutTitle.OrderId}", TestContext.Current.CancellationToken);
        Assert.Null(plain!.Order.Shipping.ExternalShippingTitle);
    }

    private sealed record ExternalSetup(
        Guid TenantId, HttpClient Client, Guid ClientId, Guid ProductId, Guid AdvisorId,
        QuotationBillingAccountRequest Billing);

    private static async Task<ExternalSetup> ArrangeAsync(QepApiFactory factory)
    {
        var (tenantId, ownerUserId, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var billing = await CreateCompanyWithBankAccountAsync(client, tenantId);
        var advisorId = await OwnerMembershipIdAsync(factory, tenantId, ownerUserId);
        return new ExternalSetup(
            tenantId, client, clientId, productId, advisorId,
            new QuotationBillingAccountRequest(billing.CompanyId, billing.BankName, billing.AccountNumber, billing.Currency));
    }

    private static ExternalOrderDraft Draft(
        ExternalSetup setup,
        string reference,
        ShippingDetails? shipping = null,
        string? externalKey = null,
        string? externalShippingTitle = null) =>
        new(
            setup.TenantId, OrderSource.Shopify, reference, setup.ClientId, setup.AdvisorId, null, null, null,
            setup.Billing, [new ExternalOrderLine(setup.ProductId, setup.ProductId, 2m, 11_900m, 0m, 19)],
            OrderPaymentStatus.PaymentPending, DateTimeOffset.UtcNow, shipping, externalKey, externalShippingTitle);

    // Un scope por llamada, como el worker: cada una con su DbContext.
    private static async Task<ExternalOrderResult> CreateAsync(QepApiFactory factory, ExternalOrderDraft draft)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var writer = scope.ServiceProvider.GetRequiredService<IExternalOrderWriter>();
        return await writer.CreateAsync(draft, TestContext.Current.CancellationToken);
    }

    private static async Task<int> CountShopifyOrdersAsync(ExternalSetup setup)
    {
        var page = await setup.Client.GetFromJsonAsync<OrdersPageResponse>(
            $"{OrdersUrl(setup.TenantId)}?source=Shopify", TestContext.Current.CancellationToken);
        return page!.Total;
    }

    // La membresía activa del owner, por el puerto de Tenancy: el roster HTTP exige un permiso que
    // ManagerPermissions no concede, y lo que se prueba acá no es ese endpoint.
    private static async Task<Guid> OwnerMembershipIdAsync(QepApiFactory factory, Guid tenantId, Guid ownerUserId)
    {
        using var scope = factory.Services.CreateScope();
        var directory = scope.ServiceProvider.GetRequiredService<IMembershipDirectory>();
        var membershipId = await directory.FindActiveMembershipIdAsync(ownerUserId, tenantId, TestContext.Current.CancellationToken);
        Assert.NotNull(membershipId);
        return membershipId.Value;
    }
}
