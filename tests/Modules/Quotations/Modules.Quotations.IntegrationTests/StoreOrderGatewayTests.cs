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
        (Guid CompanyId, string BankName, string AccountNumber, string Currency) account,
        Guid variantId,
        StoreOrderParty billing,
        StoreOrderParty? shipping = null,
        Guid? clientRequestId = null,
        decimal quantity = 2m,
        bool withRetention = false) =>
        new(
            tenantId, clientRequestId ?? Guid.CreateVersion7(), clientId, advisorId, IsRetail: true,
            new QuotationBillingAccountRequest(account.CompanyId, account.BankName, account.AccountNumber, account.Currency),
            billing, shipping ?? billing, withRetention, BillingVatSurplus: false,
            [new StoreQuoteLine(variantId, quantity)], "Entregar en portería");

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
            Draft(tenantId, customerId, advisorId, account, variantId, ColombianParty(city, "CC", "1020304050")),
            TestContext.Current.CancellationToken));

        Assert.False(result.AlreadyExisted);
        var detail = await client.GetFromJsonAsync<OrderDetailResponse>(
            $"{OrdersUrl(tenantId)}/{result.OrderId}", TestContext.Current.CancellationToken);
        Assert.NotNull(detail);
        Assert.Equal(
            ("Store", false, "PaymentPending", advisorId, customerId),
            (detail.Order.Source, detail.Order.IsStorePickup, detail.Order.PaymentStatus, detail.Order.AdvisorId, result.ClientId));
        var created = Assert.Single(detail.History, entry => entry.EventType == "Created");
        Assert.Equal("Creado desde la tienda.", created.Details);
        // Lo crea el sistema desde la tienda: ninguna persona lo firmó.
        Assert.Null(created.MemberId);
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
            tenantId, customerId, advisorId, account, variantId, billing, shipping, withRetention: true) with
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
                tenantId, customerId, advisorId, account, variantId,
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
                    tenantId, customerId, advisorId, account, variantId,
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
    public async Task AForeignTaxIdBillingPartyRoundTrips()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, ownerUserId, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var customerId = await CreateActiveCustomerAsync(client, tenantId);
        var variantId = await CreateProductWithScalesAsync(client, tenantId);
        var account = await CreateCompanyWithBankAccountAsync(client, tenantId);
        var advisorId = await AdvisorAsync(factory, tenantId, ownerUserId);
        var billing = ForeignParty() with
        {
            Country = "US",
            CityName = "Miami",
            IdentificationType = "TAX_ID",
            IdentificationNumber = "12-3456789",
        };

        var result = await WithGatewayAsync(factory, gateway => gateway.CreateAsync(
            Draft(tenantId, customerId, advisorId, account, variantId, billing),
            TestContext.Current.CancellationToken));

        var order = await LoadOrderAsync(factory, tenantId, result.OrderId);
        Assert.Equal(
            (PartyIdentificationType.TaxId, "US", "12-3456789"),
            (order.Billing!.IdentificationType, order.Billing.Country, order.Billing.IdentificationNumber));
    }

    [Fact]
    public async Task AColombianTypeAbroadIsRejectedAndCreatesNoOrder()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, ownerUserId, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var customerId = await CreateActiveCustomerAsync(client, tenantId);
        var variantId = await CreateProductWithScalesAsync(client, tenantId);
        var account = await CreateCompanyWithBankAccountAsync(client, tenantId);
        var advisorId = await AdvisorAsync(factory, tenantId, ownerUserId);
        var billing = ForeignParty() with { IdentificationType = "CC", IdentificationNumber = "1020304050" };

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() => WithGatewayAsync(factory, gateway => gateway.CreateAsync(
            Draft(tenantId, customerId, advisorId, account, variantId, billing),
            TestContext.Current.CancellationToken)));

        Assert.Equal("quotations.party.document_type_not_allowed", error.Code);
        Assert.Equal(0, await OrderRowsAsync(database.GetConnectionString(), tenantId));
    }

    // Spec 2026-11-08: la regla corre al escribir, no al cargar. Una fila guardada antes de ella (acá, un CC con
    // país ES puesto a mano en la base) se lee igual y sólo falla cuando alguien guarda la parte de nuevo.
    [Fact]
    public async Task ANonConformingStoredBillingPartyStillReadsAndOnlyFailsWhenSaved()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, ownerUserId, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var customerId = await CreateActiveCustomerAsync(client, tenantId);
        var variantId = await CreateProductWithScalesAsync(client, tenantId);
        var account = await CreateCompanyWithBankAccountAsync(client, tenantId);
        var advisorId = await AdvisorAsync(factory, tenantId, ownerUserId);
        var billing = ForeignParty() with { IdentificationType = "NIF", IdentificationNumber = "12345678Z" };
        var result = await WithGatewayAsync(factory, gateway => gateway.CreateAsync(
            Draft(tenantId, customerId, advisorId, account, variantId, billing),
            TestContext.Current.CancellationToken));
        await using (var connection = new NpgsqlConnection(database.GetConnectionString()))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = new NpgsqlCommand(
                "UPDATE quotations.order_parties SET identification_type = 'Cc' WHERE order_id = @order AND role = 'Billing'",
                connection);
            command.Parameters.AddWithValue("order", result.OrderId);
            Assert.Equal(1, await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        }

        var order = await LoadOrderAsync(factory, tenantId, result.OrderId);
        var detail = await client.GetAsync($"{OrdersUrl(tenantId)}/{result.OrderId}", TestContext.Current.CancellationToken);

        Assert.Equal(PartyIdentificationType.Cc, order.Billing!.IdentificationType);
        Assert.Equal(System.Net.HttpStatusCode.OK, detail.StatusCode);
        var error = Assert.Throws<QuotationsDomainException>(() => order.Billing.Apply(order.Billing.ToDetails()));
        Assert.Equal("quotations.party.document_type_not_allowed", error.Code);
    }

    [Fact]
    public async Task CreateIsIdempotentByClientRequestId()
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
            tenantId, customerId, advisorId, account, variantId, ColombianParty(city, "CC", "1020304050"));

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
            draft with { ClientRequestId = Guid.CreateVersion7() }, TestContext.Current.CancellationToken));
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
                Draft(tenantId, customerId, advisorId, account, variantId, ColombianParty(city, "CC", "1020304050")),
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
            Draft(tenantId, customerId, advisorId, account, variantId, ColombianParty(city, "CC", "1020304050")),
            TestContext.Current.CancellationToken));

        var created = Assert.Single(await OutboxMessagesAsync(factory, "quotations.order.created.v1"));
        using var payload = JsonDocument.Parse(created.PayloadJson);
        Assert.Equal("Store", payload.RootElement.GetProperty("source").GetString());
        // Nadie toma el pedido: la auditoría la firma el sistema como integración, sin persona detrás.
        var audit = Assert.Single(
            await OutboxMessagesAsync(factory, "platform.audit.recorded.v1"),
            message => message.PayloadJson.Contains("quotation.order.created", StringComparison.Ordinal));
        using var auditPayload = JsonDocument.Parse(audit.PayloadJson);
        Assert.Equal("Integration", auditPayload.RootElement.GetProperty("actorType").GetString());
        Assert.DoesNotContain(ownerUserId.ToString(), audit.PayloadJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheExternalKeyCarriesTheClientRequestId()
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
        var clientRequestId = Guid.CreateVersion7();

        var result = await WithGatewayAsync(factory, gateway => gateway.CreateAsync(
            Draft(
                tenantId, customerId, advisorId, account, variantId, ColombianParty(city, "CC", "1020304050"),
                clientRequestId: clientRequestId),
            TestContext.Current.CancellationToken));

        var order = await LoadOrderAsync(factory, tenantId, result.OrderId);
        // El pedido no nace de una solicitud: no hay número que mostrar como referencia.
        Assert.Equal($"store-checkout:{clientRequestId}", order.ExternalKey);
        Assert.Null(order.ExternalReference);
    }

    [Fact]
    public async Task FindExistingFindsByClientRequestId()
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
        var clientRequestId = Guid.CreateVersion7();
        var created = await WithGatewayAsync(factory, gateway => gateway.CreateAsync(
            Draft(
                tenantId, customerId, advisorId, account, variantId, ColombianParty(city, "CC", "1020304050"),
                clientRequestId: clientRequestId),
            TestContext.Current.CancellationToken));

        var found = await WithGatewayAsync(factory, gateway => gateway.FindExistingAsync(
            tenantId, clientRequestId, TestContext.Current.CancellationToken));
        var missing = await WithGatewayAsync(factory, gateway => gateway.FindExistingAsync(
            tenantId, Guid.CreateVersion7(), TestContext.Current.CancellationToken));

        Assert.NotNull(found);
        Assert.Equal((created.OrderId, true), (found.OrderId, found.AlreadyExisted));
        Assert.Null(missing);
    }

    // El tope diario cuenta todo pedido de la tienda que entró, también el anulado: anular no devuelve el cupo.
    [Fact]
    public async Task CountBySourceSinceCountsCancelledStoreOrders()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, ownerUserId, client) = await RegisterTenantAsync(
            factory, [.. ManagerPermissions, OrdersPermissions.OrderCancel]);
        using var _ = client;
        var customerId = await CreateActiveCustomerAsync(client, tenantId);
        var variantId = await CreateProductWithScalesAsync(client, tenantId);
        var account = await CreateCompanyWithBankAccountAsync(client, tenantId);
        var advisorId = await AdvisorAsync(factory, tenantId, ownerUserId);
        var (city, _) = await EnsureCityIdsInTwoDepartmentsAsync(client);
        var draft = Draft(tenantId, customerId, advisorId, account, variantId, ColombianParty(city, "CC", "1020304050"));
        await WithGatewayAsync(factory, gateway => gateway.CreateAsync(draft, TestContext.Current.CancellationToken));
        var cancelled = await WithGatewayAsync(factory, gateway => gateway.CreateAsync(
            draft with { ClientRequestId = Guid.CreateVersion7() }, TestContext.Current.CancellationToken));
        var cancel = await client.PostAsJsonAsync(
            $"{OrdersUrl(tenantId)}/{cancelled.OrderId}/cancel",
            new CancelOrderRequest("El cliente desistió"),
            TestContext.Current.CancellationToken);
        cancel.EnsureSuccessStatusCode();
        var manual = await client.PostAsJsonAsync(
            OrdersUrl(tenantId),
            new CreateOrderRequest(
                customerId, null, null, false, null, null,
                new QuotationBillingAccountRequest(account.CompanyId, account.BankName, account.AccountNumber, account.Currency),
                [new OrderEditItemRequest(variantId, 1m)], "PaymentPending", []),
            TestContext.Current.CancellationToken);
        manual.EnsureSuccessStatusCode();
        var now = DateTimeOffset.UtcNow;

        var sinceYesterday = await WithGatewayAsync(factory, gateway => gateway.CountStoreOrdersSinceAsync(
            tenantId, now.AddDays(-1), TestContext.Current.CancellationToken));
        var sinceTomorrow = await WithGatewayAsync(factory, gateway => gateway.CountStoreOrdersSinceAsync(
            tenantId, now.AddDays(1), TestContext.Current.CancellationToken));

        Assert.Equal((2, 0), (sinceYesterday, sinceTomorrow));
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
            Draft(tenantId, customerId, advisorId, account, variantId, ColombianParty(city, "CC", "1020304050")),
            TestContext.Current.CancellationToken));

        var order = await LoadOrderAsync(factory, tenantId, result.OrderId);
        Assert.Equal("EUR", order.Currency);
        Assert.Equal(25m, order.Items.Single().UnitPrice);
    }
}
