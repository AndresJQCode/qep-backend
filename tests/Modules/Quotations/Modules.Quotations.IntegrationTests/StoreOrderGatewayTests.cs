using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;
using Npgsql;
using Testcontainers.PostgreSql;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>El puerto de la tienda pública hacia el motor de pedidos (spec de la tienda §5): cotiza y crea el pedido
/// de una solicitud contra Postgres, con catálogo, cliente y empresa sembrados.</summary>
public sealed class StoreOrderGatewayTests
{
    private static string OrdersUrl(Guid tenantId) => $"/api/v1/tenants/{tenantId}/orders";

    private static StoreOrderParty ColombianParty(
        CityInDepartment city, string? identificationType = null, string? identificationNumber = null) =>
        new(
            "Ana Pérez", "+573001112233", "ana@example.com", "Calle 1 # 2-3", "CO", city.DepartmentId, city.CityId,
            null, identificationType, identificationNumber);

    private static StoreOrderParty ForeignParty() =>
        new("Ana Pérez", "+34911222333", "ana@example.com", "Calle Mayor 1", "ES", null, null, "Madrid", null, null);

    private static StoreOrderDraft Draft(
        Guid tenantId,
        Guid clientId,
        Guid advisorId,
        Guid takerUserId,
        (Guid CompanyId, string BankName, string AccountNumber, string Currency) account,
        Guid variantId,
        StoreOrderParty billing,
        StoreOrderParty? shipping = null,
        Guid? requestId = null,
        decimal quantity = 2m,
        bool withRetention = false) =>
        new(
            tenantId, requestId ?? Guid.CreateVersion7(), "T-0001", clientId, advisorId, IsRetail: true,
            new QuotationBillingAccountRequest(account.CompanyId, account.BankName, account.AccountNumber, account.Currency),
            billing, shipping ?? billing, withRetention, BillingVatSurplus: false,
            [new StoreQuoteLine(variantId, quantity)], "Entregar en portería", takerUserId);

    private static async Task<Guid> AdvisorAsync(QepApiFactory factory, Guid tenantId, Guid ownerUserId)
    {
        using var scope = factory.Services.CreateScope();
        var directory = scope.ServiceProvider.GetRequiredService<IMembershipDirectory>();
        var membershipId = await directory.FindActiveMembershipIdAsync(ownerUserId, tenantId, TestContext.Current.CancellationToken);
        Assert.NotNull(membershipId);
        return membershipId.Value;
    }

    private static async Task<Order> LoadOrderAsync(QepApiFactory factory, Guid tenantId, Guid orderId)
    {
        using var scope = factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IOrderRepository>();
        var order = await repository.FindByIdAsync(tenantId, new OrderId(orderId), TestContext.Current.CancellationToken);
        return Assert.IsType<Order>(order);
    }

    private static async Task<T> WithGatewayAsync<T>(QepApiFactory factory, Func<IStoreOrderGateway, Task<T>> action)
    {
        using var scope = factory.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<IStoreOrderGateway>());
    }

    private static async Task<long> OrderRowsAsync(string connectionString, Guid tenantId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM quotations.orders WHERE tenant_id = @tenant", connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    [Fact]
    public async Task CreateMakesAPendingDeliveredStoreOrderForAnIncompleteCustomer()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, ownerUserId, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var customerId = await CreateActiveCustomerAsync(client, tenantId);
        await IncompleteCustomerQuotationApiTests.MarkIncompleteAsync(database.GetConnectionString(), customerId);
        var variantId = await CreateProductWithScalesAsync(client, tenantId);
        var account = await CreateCompanyWithBankAccountAsync(client, tenantId);
        var advisorId = await AdvisorAsync(factory, tenantId, ownerUserId);
        var (city, _) = await EnsureCityIdsInTwoDepartmentsAsync(client);

        var result = await WithGatewayAsync(factory, gateway => gateway.CreateAsync(
            Draft(tenantId, customerId, advisorId, ownerUserId, account, variantId, ColombianParty(city, "CC", "1020304050")),
            TestContext.Current.CancellationToken));

        Assert.False(result.AlreadyExisted);
        var detail = await client.GetFromJsonAsync<OrderDetailResponse>(
            $"{OrdersUrl(tenantId)}/{result.OrderId}", TestContext.Current.CancellationToken);
        Assert.NotNull(detail);
        Assert.Equal(
            ("Store", false, "PaymentPending", advisorId, customerId),
            (detail.Order.Source, detail.Order.IsStorePickup, detail.Order.PaymentStatus, detail.Order.AdvisorId, result.ClientId));
        var created = Assert.Single(detail.History, entry => entry.EventType == "Created");
        Assert.Equal("Creado desde la tienda (solicitud T-0001).", created.Details);
        // El que toma la solicitud es el asesor: la entrada no puede quedar sin autor.
        Assert.Equal(advisorId, created.MemberId);
    }

    [Fact]
    public async Task CreateStoresTheBillingAndShippingParties()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, ownerUserId, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var customerId = await CreateActiveCustomerAsync(client, tenantId);
        var variantId = await CreateProductWithScalesAsync(client, tenantId);
        var account = await CreateCompanyWithBankAccountAsync(client, tenantId);
        var advisorId = await AdvisorAsync(factory, tenantId, ownerUserId);
        var (first, second) = await EnsureCityIdsInTwoDepartmentsAsync(client);
        var billing = ColombianParty(first, "CC", "1020304050");
        var shipping = ColombianParty(second) with { Name = "Portería", Address = "Carrera 9 # 8-7" };

        var draft = Draft(
            tenantId, customerId, advisorId, ownerUserId, account, variantId, billing, shipping, withRetention: true) with
        {
            BillingVatSurplus = true,
        };
        var result = await WithGatewayAsync(factory, gateway => gateway.CreateAsync(draft, TestContext.Current.CancellationToken));

        var order = await LoadOrderAsync(factory, tenantId, result.OrderId);
        // Colombia se guarda como null: una sola forma para CO (QuotationPartyDetails.cs:108-125).
        Assert.Equal(
            ("Ana Pérez", "1020304050", PartyIdentificationType.Cc, "ana@example.com", first.CityId, (string?)null),
            (order.Billing!.Name, order.Billing.IdentificationNumber, order.Billing.IdentificationType, order.Billing.Email,
                order.Billing.CityId, order.Billing.Country));
        Assert.Equal(
            ("Portería", "Carrera 9 # 8-7", second.CityId, second.DepartmentId),
            (order.Shipping!.Name, order.Shipping.Address, order.Shipping.CityId, order.Shipping.DepartmentId));
        Assert.Equal((true, true), (order.PartyWithRetention, order.PartyVatSurplus));
    }

    [Fact]
    public async Task CreateStoresAForeignShippingParty()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, ownerUserId, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var customerId = await CreateActiveCustomerAsync(client, tenantId);
        var variantId = await CreateProductWithScalesAsync(client, tenantId);
        var account = await CreateCompanyWithBankAccountAsync(client, tenantId);
        var advisorId = await AdvisorAsync(factory, tenantId, ownerUserId);
        var (city, _) = await EnsureCityIdsInTwoDepartmentsAsync(client);

        var result = await WithGatewayAsync(factory, gateway => gateway.CreateAsync(
            Draft(
                tenantId, customerId, advisorId, ownerUserId, account, variantId,
                ColombianParty(city, "CC", "1020304050"), ForeignParty()),
            TestContext.Current.CancellationToken));

        var order = await LoadOrderAsync(factory, tenantId, result.OrderId);
        Assert.Equal(("ES", "Madrid", null, null), (order.Shipping!.Country, order.Shipping.CityName, order.Shipping.CityId, order.Shipping.DepartmentId));
    }

    [Fact]
    public async Task ABillingPartyWithRetentionAppliesRetention()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, ownerUserId, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var customerId = await CreateActiveCustomerAsync(client, tenantId);
        var variantId = await CreateProductWithScalesAsync(client, tenantId);
        var account = await CreateCompanyWithBankAccountAsync(client, tenantId);
        var advisorId = await AdvisorAsync(factory, tenantId, ownerUserId);
        var (city, _) = await EnsureCityIdsInTwoDepartmentsAsync(client);

        var (quote, result) = await WithGatewayAsync(factory, async gateway =>
        {
            var quoted = await gateway.QuoteAsync(
                new StoreQuoteRequest(tenantId, "COP", true, [new StoreQuoteLine(variantId, 10m)]),
                TestContext.Current.CancellationToken);
            var created = await gateway.CreateAsync(
                Draft(
                    tenantId, customerId, advisorId, ownerUserId, account, variantId,
                    ColombianParty(city, "CC", "1020304050"), quantity: 10m, withRetention: true),
                TestContext.Current.CancellationToken);
            return (quoted, created);
        });

        var order = await LoadOrderAsync(factory, tenantId, result.OrderId);
        Assert.True(order.AppliesRetention);
        // La cotización no conoce al comprador, así que no retiene: el neto del pedido es lo que se aparta.
        Assert.NotEqual(quote.Total, order.NetTotal);
    }

    [Fact]
    public async Task CreateIsIdempotentByRequest()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, ownerUserId, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var customerId = await CreateActiveCustomerAsync(client, tenantId);
        var variantId = await CreateProductWithScalesAsync(client, tenantId);
        var account = await CreateCompanyWithBankAccountAsync(client, tenantId);
        var advisorId = await AdvisorAsync(factory, tenantId, ownerUserId);
        var (city, _) = await EnsureCityIdsInTwoDepartmentsAsync(client);
        var draft = Draft(
            tenantId, customerId, advisorId, ownerUserId, account, variantId, ColombianParty(city, "CC", "1020304050"));

        var first = await WithGatewayAsync(factory, gateway => gateway.CreateAsync(draft, TestContext.Current.CancellationToken));
        // El reintento llega con otro cliente y otro asesor: devuelve los del pedido que ya existe (R5).
        var second = await WithGatewayAsync(factory, gateway => gateway.CreateAsync(
            draft with { ClientId = Guid.CreateVersion7(), AdvisorMembershipId = Guid.CreateVersion7() },
            TestContext.Current.CancellationToken));

        Assert.False(first.AlreadyExisted);
        Assert.True(second.AlreadyExisted);
        Assert.Equal(
            (first.OrderId, first.OrderNumber, first.ClientId, first.AdvisorMembershipId),
            (second.OrderId, second.OrderNumber, second.ClientId, second.AdvisorMembershipId));
        Assert.Equal(1, await OrderRowsAsync(database.GetConnectionString(), tenantId));
        // Un solo número gastado: el siguiente pedido es el consecutivo inmediato.
        var next = await WithGatewayAsync(factory, gateway => gateway.CreateAsync(
            draft with { RequestId = Guid.CreateVersion7() }, TestContext.Current.CancellationToken));
        Assert.EndsWith("0002", next.OrderNumber, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCreatedOrderTotalEqualsTheQuote()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, ownerUserId, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var customerId = await CreateActiveCustomerAsync(client, tenantId);
        var variantId = await CreateProductWithScalesAsync(client, tenantId);
        var account = await CreateCompanyWithBankAccountAsync(client, tenantId);
        var advisorId = await AdvisorAsync(factory, tenantId, ownerUserId);
        var (city, _) = await EnsureCityIdsInTwoDepartmentsAsync(client);

        var (quote, result) = await WithGatewayAsync(factory, async gateway =>
        {
            var quoted = await gateway.QuoteAsync(
                new StoreQuoteRequest(tenantId, "COP", true, [new StoreQuoteLine(variantId, 2m)]),
                TestContext.Current.CancellationToken);
            var created = await gateway.CreateAsync(
                Draft(tenantId, customerId, advisorId, ownerUserId, account, variantId, ColombianParty(city, "CC", "1020304050")),
                TestContext.Current.CancellationToken);
            return (quoted, created);
        });

        Assert.Equal((quote.Total, quote.Currency), (result.Total, result.Currency));
    }

    [Fact]
    public async Task CreatePublishesOrderCreatedAndAudits()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, ownerUserId, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var customerId = await CreateActiveCustomerAsync(client, tenantId);
        var variantId = await CreateProductWithScalesAsync(client, tenantId);
        var account = await CreateCompanyWithBankAccountAsync(client, tenantId);
        var advisorId = await AdvisorAsync(factory, tenantId, ownerUserId);
        var (city, _) = await EnsureCityIdsInTwoDepartmentsAsync(client);

        await WithGatewayAsync(factory, gateway => gateway.CreateAsync(
            Draft(tenantId, customerId, advisorId, ownerUserId, account, variantId, ColombianParty(city, "CC", "1020304050")),
            TestContext.Current.CancellationToken));

        var created = Assert.Single(await OutboxMessagesAsync(factory, "quotations.order.created.v1"));
        using var payload = JsonDocument.Parse(created.PayloadJson);
        Assert.Equal("Store", payload.RootElement.GetProperty("source").GetString());
        // La auditoría la firma la persona que tomó la solicitud, no una integración.
        var audit = Assert.Single(
            await OutboxMessagesAsync(factory, "platform.audit.recorded.v1"),
            message => message.PayloadJson.Contains("quotation.order.created", StringComparison.Ordinal));
        using var auditPayload = JsonDocument.Parse(audit.PayloadJson);
        Assert.NotEqual("Integration", auditPayload.RootElement.GetProperty("actorType").GetString());
        Assert.Contains(ownerUserId.ToString(), audit.PayloadJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheCreatedOrderIsInItsAccountsCurrency()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, ownerUserId, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var customerId = await CreateActiveCustomerAsync(client, tenantId);
        var variantId = await CreateProductWithScalesAsync(
            client, tenantId, prices: new Dictionary<string, decimal> { ["COP"] = 100_000m, ["EUR"] = 25m });
        var account = await CreateCompanyWithBankAccountAsync(client, tenantId, currency: "EUR");
        var advisorId = await AdvisorAsync(factory, tenantId, ownerUserId);
        var (city, _) = await EnsureCityIdsInTwoDepartmentsAsync(client);

        var result = await WithGatewayAsync(factory, gateway => gateway.CreateAsync(
            Draft(tenantId, customerId, advisorId, ownerUserId, account, variantId, ColombianParty(city, "CC", "1020304050")),
            TestContext.Current.CancellationToken));

        var order = await LoadOrderAsync(factory, tenantId, result.OrderId);
        Assert.Equal("EUR", order.Currency);
        Assert.Equal(25m, order.Items.Single().UnitPrice);
    }
}
