using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Pos.Application;
using Testcontainers.PostgreSql;
using static Modules.Pos.IntegrationTests.PosApiHarness;

namespace Modules.Pos.IntegrationTests;

/// <summary>Spec 2026-10-14 «Endpoints nuevos»: el selector y el alta rápida de la caja (POSC-05).</summary>
public sealed class PosCustomerApiTests
{
    // Permisos de Customers a mano: Pos.IntegrationTests no referencia ese módulo, y un cliente completo o inactivo
    // sólo se puede sembrar por su API (el selector tiene que verlos tal como los deja Customers).
    private static readonly string[] CustomersAdmin =
    [
        "customers.customer.read",
        "customers.customer.manage",
        "customers.classification.read",
        "customers.classification.manage",
    ];

    private static readonly string[] BillingLike =
    [
        PosPermissions.SaleRead,
        PosPermissions.SaleCreate,
        PosPermissions.SaleVoid,
        PosPermissions.RegisterRead,
    ];

    private sealed record Arranged(PosWorld World, HttpClient Customers, HttpClient Cashier);

    private static async Task<Arranged> ArrangeAsync(QepApiFactory factory, PostgreSqlContainer database)
    {
        var world = await PosWorld.ArrangeAsync(factory, database);
        var customers = CreateClient(factory, world.Tenant.OwnerUserId, world.Tenant.TenantId, [.. CustomersAdmin, .. SeedPermissions]);
        var cashier = await InviteCashierAsync(factory, database, world.Tenant, CashierPermissions);
        return new Arranged(world, customers, cashier.Client);
    }

    private static string CustomersUrl(Guid tenantId) => $"/api/v1/tenants/{tenantId}/customers";

    private static async Task<Guid> CreateCompleteCustomerAsync(
        HttpClient customers, Guid tenantId, string name, string number, Guid? classificationId = null)
    {
        var cityId = await EnsureCityIdAsync(customers);
        classificationId ??= await CreateClassificationAsync(customers, tenantId);
        var response = await customers.PostAsJsonAsync(
            CustomersUrl(tenantId),
            new
            {
                name,
                identificationType = "CC",
                identificationNumber = number,
                phone = "310 935 2187",
                email = "compras@verde.co",
                address = "Calle 10 # 45-12",
                country = "CO",
                cityId,
                classificationId,
                withRetention = false,
                vatSurplus = false,
            },
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> CreateClassificationAsync(HttpClient customers, Guid tenantId)
    {
        var response = await customers.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/customers/classifications",
            new { name = "Mediano", prefix = "CLI" },
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("id").GetGuid();
    }

    // Un cliente que nació por WhatsApp: sin documento, incompleto (precedente: IncompleteCustomerReportApiTests).
    private static Task SeedWhatsAppCustomerAsync(PostgreSqlContainer database, Guid tenantId, string name) =>
        ExecuteSqlAsync(
            database,
            """
            INSERT INTO customers.customers (id, tenant_id, name, is_active, completeness, whatsapp_user_id, with_retention, vat_surplus, version, created_at, updated_at)
            VALUES (gen_random_uuid(), @tenantId, @name, true, 'Incomplete', 'CO.1', false, false, 1, now(), now())
            """,
            ("tenantId", tenantId),
            ("name", name));

    private static object QuickBody(string name = "Laura Gómez", string number = "1020304050") =>
        new { name, identificationType = "CC", identificationNumber = number, phone = (string?)null };

    [Fact]
    public async Task TheCashierSearchesOnlyActiveCustomersWithDocument()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var arranged = await ArrangeAsync(factory, database);
        var tenantId = arranged.World.Tenant.TenantId;
        var classification = await CreateClassificationAsync(arranged.Customers, tenantId);
        var complete = await CreateCompleteCustomerAsync(arranged.Customers, tenantId, "Verde Esencial", "1020304050", classification);
        await SeedWhatsAppCustomerAsync(database, tenantId, "Laura de WhatsApp");
        var inactive = await CreateCompleteCustomerAsync(arranged.Customers, tenantId, "Pedro Inactivo", "5566778899", classification);
        (await arranged.Customers.PostAsync($"{CustomersUrl(tenantId)}/{inactive}/deactivate", null, TestContext.Current.CancellationToken))
            .EnsureSuccessStatusCode();

        var response = await arranged.Cashier.GetAsync($"{arranged.World.Url}/customers?search=", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var item = Assert.Single(body.GetProperty("items").EnumerateArray());
        Assert.Equal(complete, item.GetProperty("id").GetGuid());
        Assert.Equal("Verde Esencial", item.GetProperty("name").GetString());
        Assert.True(item.GetProperty("isComplete").GetBoolean());
        Assert.True(item.GetProperty("isActive").GetBoolean());
        // El cajero no tiene el padrón: ni teléfono, ni correo, ni dirección.
        Assert.Equal(
            ["id", "identificationNumber", "identificationType", "isActive", "isComplete", "name"],
            item.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task TheCashierQuickCreatesAnIncompleteCustomer()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var arranged = await ArrangeAsync(factory, database);
        var tenantId = arranged.World.Tenant.TenantId;

        var response = await arranged.Cashier.PostAsJsonAsync($"{arranged.World.Url}/customers", QuickBody(), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var id = body.GetProperty("id").GetGuid();
        Assert.Equal($"/api/v1/tenants/{tenantId}/pos/customers/{id}", response.Headers.Location?.OriginalString);
        Assert.Equal("Laura Gómez", body.GetProperty("name").GetString());
        Assert.Equal("CC", body.GetProperty("identificationType").GetString());
        Assert.Equal("1020304050", body.GetProperty("identificationNumber").GetString());
        Assert.False(body.GetProperty("isComplete").GetBoolean());
        Assert.True(body.GetProperty("isActive").GetBoolean());
        var detail = await arranged.Customers.GetFromJsonAsync<JsonElement>(
            $"{CustomersUrl(tenantId)}/{id}", TestContext.Current.CancellationToken);
        Assert.False(detail.GetProperty("isComplete").GetBoolean());
        // La auditoría de Customers lleva source = 'pos' cuando el alta sale de la caja.
        Assert.Equal(1L, await AuditCountAsync(database, tenantId, "customers.customer.created"));
    }

    [Fact]
    public async Task QuickCreateWithATakenDocumentReturnsTheExistingCustomer()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var arranged = await ArrangeAsync(factory, database);
        var existing = await CreateCompleteCustomerAsync(arranged.Customers, arranged.World.Tenant.TenantId, "Verde Esencial", "1020304050");

        var response = await arranged.Cashier.PostAsJsonAsync($"{arranged.World.Url}/customers", QuickBody(), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("pos.customer.identification_taken", problem.GetProperty("code").GetString());
        var shown = problem.GetProperty("existing");
        Assert.Equal(existing, shown.GetProperty("id").GetGuid());
        Assert.Equal("Verde Esencial", shown.GetProperty("name").GetString());
        Assert.True(shown.GetProperty("isActive").GetBoolean());
    }

    [Fact]
    public async Task QuickCreateWithTheDocumentOfAnInactiveCustomerReturnsItAsExisting()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var arranged = await ArrangeAsync(factory, database);
        var tenantId = arranged.World.Tenant.TenantId;
        var existing = await CreateCompleteCustomerAsync(arranged.Customers, tenantId, "Pedro Inactivo", "1020304050");
        (await arranged.Customers.PostAsync($"{CustomersUrl(tenantId)}/{existing}/deactivate", null, TestContext.Current.CancellationToken))
            .EnsureSuccessStatusCode();

        var response = await arranged.Cashier.PostAsJsonAsync($"{arranged.World.Url}/customers", QuickBody(), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var shown = (await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("existing");
        Assert.Equal(existing, shown.GetProperty("id").GetGuid());
        Assert.False(shown.GetProperty("isActive").GetBoolean());
        Assert.Equal(
            1L,
            await CountAsync(database, "SELECT count(*) FROM customers.customers WHERE tenant_id = @tenantId", ("tenantId", tenantId)));
    }

    [Fact]
    public async Task QuickCreateValidationErrorsComeByField()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var arranged = await ArrangeAsync(factory, database);

        var response = await arranged.Cashier.PostAsJsonAsync(
            $"{arranged.World.Url}/customers", QuickBody(name: string.Empty), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("validation.failed", problem.GetProperty("code").GetString());
        Assert.True(problem.GetProperty("errors").TryGetProperty("Name", out _));
    }

    [Fact]
    public async Task TheTakenProblemKeepsCodeTraceIdAndAddsExisting()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var arranged = await ArrangeAsync(factory, database);
        await CreateCompleteCustomerAsync(arranged.Customers, arranged.World.Tenant.TenantId, "Verde Esencial", "1020304050");

        var response = await arranged.Cashier.PostAsJsonAsync($"{arranged.World.Url}/customers", QuickBody(), TestContext.Current.CancellationToken);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.True(problem.TryGetProperty("code", out _));
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("traceId").GetString()));
        Assert.True(problem.TryGetProperty("existing", out _));
        Assert.False(problem.TryGetProperty("errors", out _));
    }

    [Fact]
    public async Task WithoutTheCustomersModuleTheCustomerPathIs403AndPlainSalesStillWork()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var arranged = await ArrangeAsync(factory, database);
        await DisableModuleAsync(database, arranged.World.Tenant.TenantId, "customers");

        var search = await arranged.Cashier.GetAsync($"{arranged.World.Url}/customers", TestContext.Current.CancellationToken);
        var create = await arranged.Cashier.PostAsJsonAsync($"{arranged.World.Url}/customers", QuickBody(), TestContext.Current.CancellationToken);

        // Sólo el status: la política enmascarada no garantiza código.
        Assert.Equal(HttpStatusCode.Forbidden, search.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);

        var session = await arranged.World.OpenSessionAsync(arranged.Cashier);
        var withCustomer = await arranged.Cashier.PostAsJsonAsync(
            $"{arranged.World.Url}/sales",
            SaleWithCustomer(arranged.World, session.Id, Guid.CreateVersion7(), Guid.CreateVersion7()),
            TestContext.Current.CancellationToken);
        var plain = await arranged.Cashier.PostAsJsonAsync(
            $"{arranged.World.Url}/sales",
            PosWorld.SaleBody(Guid.CreateVersion7(), session.Id, arranged.World.PlainLine(), PosWorld.CashPayment(20_000m)),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, withCustomer.StatusCode);
        var problem = await withCustomer.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("pos.sale.customer_not_allowed", problem.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Created, plain.StatusCode);
        var sale = await plain.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("Consumidor final", sale.GetProperty("customer").GetProperty("name").GetString());
    }

    [Fact]
    public async Task ASaleToAQuickCreatedCustomerCarriesItInTheTicketAndTheList()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var arranged = await ArrangeAsync(factory, database);
        var created = await arranged.Cashier.PostAsJsonAsync($"{arranged.World.Url}/customers", QuickBody(), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var customerId = (await created.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("id").GetGuid();
        var session = await arranged.World.OpenSessionAsync(arranged.Cashier);
        var saleId = Guid.CreateVersion7();

        var response = await arranged.Cashier.PostAsJsonAsync(
            $"{arranged.World.Url}/sales",
            SaleWithCustomer(arranged.World, session.Id, saleId, customerId),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var customer = (await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("customer");
        Assert.Equal("Laura Gómez", customer.GetProperty("name").GetString());
        Assert.Equal("CC", customer.GetProperty("identificationType").GetString());
        Assert.Equal("1020304050", customer.GetProperty("identificationNumber").GetString());
        var list = await arranged.Cashier.GetFromJsonAsync<JsonElement>($"{arranged.World.Url}/sales", TestContext.Current.CancellationToken);
        var item = Assert.Single(list.GetProperty("items").EnumerateArray());
        Assert.Equal("Laura Gómez", item.GetProperty("customerName").GetString());
        Assert.Equal(
            1L,
            await CountAsync(
                database,
                "SELECT count(*) FROM pos.sales WHERE id = @id AND customer_id = @customerId",
                ("id", saleId),
                ("customerId", customerId)));
    }

    private static object SaleWithCustomer(PosWorld world, Guid sessionId, Guid saleId, Guid customerId) =>
        new
        {
            id = saleId,
            cashSessionId = sessionId,
            lines = world.PlainLine(),
            payments = PosWorld.CashPayment(20_000m),
            customerId,
        };

    [Fact]
    public async Task BillingCannotUseThePosCustomerEndpoints()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var arranged = await ArrangeAsync(factory, database);
        using var billing = CreateClient(factory, arranged.World.Tenant.OwnerUserId, arranged.World.Tenant.TenantId, BillingLike);

        var search = await billing.GetAsync($"{arranged.World.Url}/customers", TestContext.Current.CancellationToken);
        var create = await billing.PostAsJsonAsync($"{arranged.World.Url}/customers", QuickBody(), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, search.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
    }
}
