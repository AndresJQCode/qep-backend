using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Npgsql;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>Spec 2026-11-18 §5.5: la marca de proveedor del pedido, el puerto que lo expone a un facturador externo y el 409 de la marca
/// manual mientras hay una factura externa abierta.</summary>
public sealed class OrderExternalInvoicingApiTests(SharedQuotationsHost sharedHost) : OrderApiTests, IClassFixture<SharedQuotationsHost>
{
    // La marca la escribe SiigoOrderSource por IExternalOrderInvoicing (se prueba de punta a punta en SiigoInvoiceApiTests); acá se
    // siembra por SQL para ver lo que la API le entrega a la pantalla. El id de la factura en el proveedor no viaja.
    [Fact]
    public async Task TheOrderDtoShowsProviderAndNumber()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, InvoicerPermissions);
        using var _ = client;
        var approved = await CreateApprovedOrderAsync(client, factory, tenantId);
        await MarkInvoicedBySqlAsync(sharedHost.Database.GetConnectionString(), approved.Id);

        var response = await client.GetAsync(
            $"{OrderByIdUrl(tenantId, approved.Id)}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var order = json.RootElement.GetProperty("order");
        Assert.True(order.GetProperty("invoiced").GetBoolean());
        Assert.Equal("siigo", order.GetProperty("invoiceProvider").GetString());
        Assert.Equal("FV-1-123", order.GetProperty("invoiceNumber").GetString());
        Assert.False(order.TryGetProperty("invoiceExternalId", out var hidden), $"The external id must not travel: {hidden}");
    }

    [Fact]
    public async Task RevertingAnOrderInvoicedBySiigoIs422()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, InvoicerPermissions);
        using var _ = client;
        var approved = await CreateApprovedOrderAsync(client, factory, tenantId);
        await MarkInvoicedBySqlAsync(sharedHost.Database.GetConnectionString(), approved.Id);

        var response = await client.PostAsync(
            OrderUninvoiceUrl(tenantId, approved.Id), content: null, TestContext.Current.CancellationToken);

        await AssertDomainRejectionAsync(response, "order.order.invoiced_externally");
    }

    // Spec §5.5: con una factura de Siigo Pending o Unknown, nadie marca a mano. El adaptador real (SQL sobre siigo.invoices) se prueba en
    // SiigoInvoiceApiTests.TheRealStatusAdapterSeesOpenInvoices; acá un doble que dice «hay una abierta». Host propio: cambia el contenedor.
    [Fact]
    public async Task ManualInvoicingWithAnOpenSiigoInvoiceIs409()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, ownerUserId, setupClient) = await RegisterTenantAsync(factory, InvoicerPermissions);
        using var _ = setupClient;
        var approved = await CreateApprovedOrderAsync(setupClient, factory, tenantId);
        using var withOpenInvoice = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IExternalInvoiceStatus>();
            services.AddScoped<IExternalInvoiceStatus, AlwaysOpenExternalInvoice>();
        }));
        using var client = withOpenInvoice.CreateClient();
        client.DefaultRequestHeaders.Add("X-Subject-Id", ownerUserId.ToString());
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenantId.ToString());
        client.DefaultRequestHeaders.Add("X-Permissions", string.Join(',', InvoicerPermissions));

        var response = await client.PostAsync(
            OrderInvoiceUrl(tenantId, approved.Id), content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("order.order.external_invoice_pending", json.RootElement.GetProperty("code").GetString());
        var fetched = await setupClient.GetFromJsonAsync<OrderDetailResponse>(
            OrderByIdUrl(tenantId, approved.Id), TestContext.Current.CancellationToken);
        Assert.False(fetched!.Order.Invoiced);
    }

    // I1: anular mientras la factura de Siigo está saliendo (o creada y sin marcar) dejaría el POST de Siigo fallando para siempre.
    [Fact]
    public async Task CancellingAnOrderWithAnOpenExternalInvoiceIs409AndLeavesItOpen()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        string[] permissions = [.. InvoicerPermissions, OrdersPermissions.OrderCancel];
        var (tenantId, ownerUserId, setupClient) = await RegisterTenantAsync(factory, permissions);
        using var _ = setupClient;
        var approved = await CreateApprovedOrderAsync(setupClient, factory, tenantId);
        using var withOpenInvoice = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IExternalInvoiceStatus>();
            services.AddScoped<IExternalInvoiceStatus, AlwaysOpenExternalInvoice>();
        }));
        using var client = withOpenInvoice.CreateClient();
        client.DefaultRequestHeaders.Add("X-Subject-Id", ownerUserId.ToString());
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenantId.ToString());
        client.DefaultRequestHeaders.Add("X-Permissions", string.Join(',', permissions));

        var response = await client.PostAsJsonAsync(
            OrderCancelUrl(tenantId, approved.Id), new CancelOrderRequest("Duplicado"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("order.order.external_invoice_pending", json.RootElement.GetProperty("code").GetString());
        var fetched = await setupClient.GetFromJsonAsync<OrderDetailResponse>(
            OrderByIdUrl(tenantId, approved.Id), TestContext.Current.CancellationToken);
        Assert.Equal("Open", fetched!.Order.Status);
    }

    [Fact]
    public async Task GetForInvoicingResolvesTheBilledPartyFromTheCustomer()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, InvoicerPermissions);
        using var _ = client;
        var approved = await CreateApprovedOrderAsync(client, factory, tenantId);
        using var scope = factory.Services.CreateScope();
        var port = scope.ServiceProvider.GetRequiredService<IExternalOrderInvoicing>();

        var order = await port.GetForInvoicingAsync(tenantId, approved.Id, TestContext.Current.CancellationToken);

        Assert.NotNull(order);
        Assert.Equal(approved.Id, order.OrderId);
        Assert.Equal(approved.OrderNumber, order.OrderNumber);
        Assert.Equal("COP", order.Currency);
        Assert.Equal(approved.Version, order.Version);
        Assert.Equal(approved.Total, order.Total);
        Assert.Equal(approved.NetTotal, order.NetTotal);
        Assert.Single(order.Lines);
        Assert.Equal(order.ProductsTotal, order.Lines.Sum(line => line.Subtotal + line.TaxAmount));
        // El pedido no tiene parte de facturación propia: la identidad sale de la ficha del cliente (spec §6.2).
        var party = order.BilledParty;
        Assert.False(party.IsFinalConsumer);
        Assert.Equal("Verde Esencial S.A.S.", party.Name);
        Assert.StartsWith("900.", party.Identification, StringComparison.Ordinal);
        Assert.Equal(PartyIdentificationType.Nit, party.IdentificationType);
        Assert.Equal("Calle 10 # 45-12", party.Address);
        Assert.Equal("compras@verde.co", party.Email);
        Assert.Equal("CO", party.Country);
        Assert.NotNull(party.CityId);
        Assert.NotNull(party.DepartmentId);
        Assert.Equal(approved.BillingAccount!.CompanyId, order.CompanyId);
        // El owner creó el pedido y es su asesor: Siigo busca el vendedor por su membresía; sin nombre cargado, la etiqueta es su correo.
        Assert.NotEqual(Guid.Empty, order.AdvisorMembershipId);
        Assert.Matches(@"^owner-[0-9a-f]{32}@example\.com$", order.AdvisorLabel);
    }

    [Fact]
    public async Task GetForInvoicingKeepsTheSameRulesAsTheManualMarkAndTreatsAnotherTenantAsMissing()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, InvoicerPermissions);
        using var _ = client;
        var approved = await CreateApprovedOrderAsync(client, factory, tenantId);
        using var scope = factory.Services.CreateScope();
        var port = scope.ServiceProvider.GetRequiredService<IExternalOrderInvoicing>();

        Assert.Null(await port.GetForInvoicingAsync(Guid.CreateVersion7(), approved.Id, TestContext.Current.CancellationToken));
        (await client.PostAsync(
            OrderInvoiceUrl(tenantId, approved.Id), content: null, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(
            () => port.GetForInvoicingAsync(tenantId, approved.Id, TestContext.Current.CancellationToken));
        Assert.Equal("order.order.already_invoiced", error.Code);
    }

    private static async Task MarkInvoicedBySqlAsync(string connectionString, Guid orderId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            UPDATE quotations.orders
               SET invoiced_at = now(), invoiced_by = @by, invoice_provider = 'siigo',
                   invoice_number = 'FV-1-123', invoice_external_id = 'a1b2c3d4'
             WHERE id = @id
            """,
            connection);
        command.Parameters.AddWithValue("id", orderId);
        command.Parameters.AddWithValue("by", Guid.CreateVersion7());
        Assert.Equal(1, await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
    }

    private sealed class AlwaysOpenExternalInvoice : IExternalInvoiceStatus
    {
        public Task<bool> HasOpenInvoiceAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken) => Task.FromResult(true);
    }
}
