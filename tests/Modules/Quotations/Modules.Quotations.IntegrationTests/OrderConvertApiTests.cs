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

/// <summary>Convertir una cotización en pedido: lo que se copia y lo que responde.</summary>
public sealed class OrderConvertApiTests : OrderApiTests
{
    // Decisión del owner 2026-10-18: las notas del pedido son un solo campo. `notes` ausente o null hereda las
    // de la cotización; con texto las reemplaza; "" las borra. Se manda JSON crudo para fijar qué viaja.
    [Theory]
    [InlineData("""{"paymentProofs":[]}""", "Nota de la cotización")]
    [InlineData("""{"notes":null,"paymentProofs":[]}""", "Nota de la cotización")]
    [InlineData("""{"notes":"Nota del pedido","paymentProofs":[]}""", "Nota del pedido")]
    [InlineData("""{"notes":"","paymentProofs":[]}""", null)]
    public async Task ConvertingKeepsASingleNotesField(string body, string? expected)
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(
            client, factory, tenantId, clientId, productId, notes: "Nota de la cotización");

        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        var response = await client.PostAsync(
            OrderUrl(tenantId, quotation.Id), content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var order = await ReadOrderAsync(response);
        Assert.Equal(expected, order.Notes);
    }

    // Review Focus 3 (spec 2026-11-10 §3.2): un frontend anterior al despliegue conjunto todavía manda
    // `paymentStatus`. El campo se ignora y el pago sale calculado: sin comprobantes, pendiente, nunca pagado.
    [Fact]
    public async Task ConvertingIgnoresAPaymentStatusSentByAnOldClient()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);

        using var content = new StringContent(
            """{"paymentStatus":"FullPaymentReceived","paymentProofs":[]}""", System.Text.Encoding.UTF8, "application/json");
        var response = await client.PostAsync(
            OrderUrl(tenantId, quotation.Id), content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var order = await ReadOrderAsync(response);
        Assert.Equal("PaymentPending", order.PaymentStatus);
    }

    // Spec 2026-10-15 §4.4: convertir copia la cotización al pedido y la deja congelada.
    [Fact]
    public async Task ConvertingCopiesTheQuotationAndFreezesIt()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);

        var response = await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest("Pago verificado", []),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var order = await ReadOrderAsync(response);
        Assert.Equal((quotation.Id, "Quotation", quotation.Total, quotation.NetTotal),
            (order.QuotationId, order.Source, order.Total, order.NetTotal));
        Assert.Equal(clientId, order.ClientId);
        Assert.Equal("Pago verificado", order.Notes);

        // La cotización queda congelada: editar una línea responde 422 not_editable.
        var itemId = Assert.Single(quotation.Items).Id;
        var edit = await client.PutAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/items/{itemId}",
            new UpdateQuotationItemRequest(5m),
            TestContext.Current.CancellationToken);
        await AssertDomainRejectionAsync(edit, "quotation.quotation.not_editable");
    }

    // Bug real, 2026-09-12: el editor de cotizaciones dejo de pedir la forma de pago hace
    // rato (el formulario del frontend ya no tiene campo que la escriba), asi que toda
    // cotizacion nueva la tiene en null -- pero `EnsureConvertibleToOrder` seguia exigiendola,
    // y con eso "Convertir en pedido" no aparecia nunca para nadie. La cobertura vieja probaba
    // lo contrario (que rechazara sin forma de pago); esta prueba el arreglo.
    // Spec 2026-10-15 §8: la pantalla de conversión dibuja el resumen con la respuesta de la
    // conversión, sin pedir el pedido ni la cotización después; por eso responde el detalle.
    [Fact]
    public async Task ConvertingAnswersTheOrderDetail()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);

        var response = await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest(null, []),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var detail = await response.Content.ReadFromJsonAsync<OrderDetailResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(detail?.Order);
        Assert.Equal(
            ((Guid?)quotation.Id, quotation.QuotationNumber, clientId),
            (detail.Order.QuotationId, detail.SourceQuotation?.QuotationNumber, detail.Client?.Id));
        Assert.Equal(productId, Assert.Single(detail.Items).ProductId);
        Assert.Equal("Created", Assert.Single(detail.History).EventType);
        Assert.Equal($"/api/v1/tenants/{tenantId}/orders/{detail.Order.Id}", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task ConvertWithoutAPaymentMethodSucceeds()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(
            client, factory, tenantId, clientId, productId, paymentMethod: null);
        var proofFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);

        var response = await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest(
                "Pago verificado",
                [new OrderPaymentProofRequest(proofFileId, quotation.Total)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    // A pedido (2026-10-08): la fecha del comprobante la escribe quien lo adjunta en el asistente y
    // viaja en `paidOn`, por comprobante, como fecha ISO sin hora. Se guarda con el comprobante y
    // vuelve tal cual, en la respuesta de la conversión y en el GET: hasta ahora el backend la
    // ignoraba y la única fecha que quedaba era la de subida.
    [Fact]
    public async Task ConvertKeepsThePaidOnDateOfEachProof()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var proofFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);

        var response = await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest(
                null,
                [new OrderPaymentProofRequest(proofFileId, quotation.Total, new DateOnly(2026, 10, 6))]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        // Como texto crudo: el contrato con el frontend es el nombre `paidOn` y la fecha ISO sin
        // hora, y deserializar al record no probaría ninguna de las dos cosas.
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var json = JsonDocument.Parse(body);
        var proofJson = Assert.Single(json.RootElement.GetProperty("order").GetProperty("paymentProofs").EnumerateArray());
        Assert.Equal("2026-10-06", proofJson.GetProperty("paidOn").GetString());

        var fetched = await GetOrderByQuotationAsync(client, tenantId, quotation.Id);
        Assert.NotNull(fetched);
        Assert.Equal(new DateOnly(2026, 10, 6), Assert.Single(fetched.PaymentProofs).PaidOn);
    }

    // Sin `paidOn` el comprobante queda sin fecha de pago: null en la respuesta, no la fecha de
    // subida disfrazada. Que sea obligatoria es una decisión pendiente (2026-10-08).
    [Fact]
    public async Task ConvertWithoutPaidOnLeavesTheProofWithoutAPaymentDate()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var proofFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);

        var response = await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest(
                null, [new OrderPaymentProofRequest(proofFileId, quotation.Total)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var order = await response.Content.ReadConvertedOrderAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(order);
        Assert.Null(Assert.Single(order.PaymentProofs).PaidOn);
    }

    // Reloj en la frontera de fin de año de Bogotá: el pedido numera con el año del tenant (spec
    // 2026-09-17, punto 2b).
    [Fact]
    public async Task ConvertCreatesTheOrderAndLeavesTheQuotationConverted()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), utcNow: NewYearsEveInBogota);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var proofFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);

        var response = await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest(
                "Pago verificado", [new OrderPaymentProofRequest(proofFileId, quotation.Total)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var order = await response.Content.ReadConvertedOrderAsync(
            TestContext.Current.CancellationToken);
        Assert.NotNull(order);
        // Nace Open (spec 2026-11-10 §3.1). El pago lo calcula el sistema: un comprobante igual al
        // total deja el pedido «Por verificar», y sólo una persona lo marca como pagado.
        Assert.Equal("Open", order.Status);
        Assert.Equal("PendingVerification", order.PaymentStatus);
        Assert.Equal(quotation.Id, order.QuotationId);
        Assert.StartsWith("PED-2026-", order.OrderNumber, StringComparison.Ordinal);
        Assert.Null(order.RitualCollectionSyncId);
        var proof = Assert.Single(order.PaymentProofs);
        Assert.Equal(proofFileId, proof.FileId);
        Assert.Equal(quotation.Total, proof.Amount);

        // Convertir deja la cotizacion en Converted, en la misma unidad de trabajo que crea el
        // pedido: de ahi en adelante es de solo lectura y ya no ofrece convertirse otra vez.
        var fetchedQuotation = await client.GetFromJsonAsync<QuotationResponse>(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}", TestContext.Current.CancellationToken);
        Assert.NotNull(fetchedQuotation);
        Assert.Equal("Converted", fetchedQuotation.Status);
        Assert.False(fetchedQuotation.CanBeConvertedToOrder);

        // Y el listado la filtra por ese estado (QuotationListing.ParseStatus, contra la base):
        // ya no aparece entre las enviadas.
        var converted = await client.GetFromJsonAsync<QuotationsPageResponse>(
            $"{QuotationsUrl(tenantId)}?status=Converted", TestContext.Current.CancellationToken);
        var sent = await client.GetFromJsonAsync<QuotationsPageResponse>(
            $"{QuotationsUrl(tenantId)}?status=Sent", TestContext.Current.CancellationToken);
        Assert.NotNull(converted);
        Assert.NotNull(sent);
        Assert.Equal(quotation.Id, Assert.Single(converted.Items).Id);
        Assert.Equal(0, sent.Total);
    }

    // US-14: sin comprobantes se permite unicamente cuando el pago queda pendiente.
    [Fact]
    public async Task ConvertWithPaymentPendingRequiresNoProofs()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);

        var response = await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest(null, []),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var order = await response.Content.ReadConvertedOrderAsync(
            TestContext.Current.CancellationToken);
        Assert.NotNull(order);
        Assert.Empty(order.PaymentProofs);
    }

    // US-13: ya no hace falta haberla enviado (a pedido, 2026-09), pero un borrador recien
    // creado por este helper no tiene productos ni cuenta de cobro -- se rechaza por eso, no
    // por seguir en Draft.
    [Fact]
    public async Task ConvertADraftQuotationIsUnprocessable()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var quotation = await CreateQuotationAsync(client, tenantId, clientId);

        var response = await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest(null, []),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task ConvertWithAZeroAmountProofIsUnprocessable()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var proofFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);

        var response = await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest(
                null, [new OrderPaymentProofRequest(proofFileId, 0m)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    // Spec 2026-10-26: convertir copia la parte de facturación con su tipo de persona y de documento.
    [Fact]
    public async Task ConvertedOrderCarriesTheBillingPartyFiscalTypes()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var home = (await EnsureCityIdsInTwoDepartmentsAsync(client)).First;
        var billing = new QuotationPartyRequest(
            "Otra persona", "1020304050", "3001234567", "otra@correo.co", "Calle 1 # 2-3", home.DepartmentId, home.CityId,
            PersonType: "Natural", IdentificationType: "CE");
        var quotation = await CreateSentQuotationAsync(
            client, factory, tenantId, clientId, productId,
            parties: new QuotationPartiesRequest(billing, null, BillingWithRetention: false, BillingVatSurplus: false));

        var response = await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest(null, []),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var order = await ReadOrderAsync(response);
        var party = Assert.Single(order.Parties, candidate => candidate.Role == "Billing");
        Assert.Equal(("Natural", "CE"), (party.PersonType, party.IdentificationType));
    }

    [Fact]
    public async Task TheOrderClientBlockCarriesCountryPersonTypeAndIdentificationType()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId, personType: "Juridica");
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var converted = await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest(null, []),
            TestContext.Current.CancellationToken);
        var order = await ReadOrderAsync(converted);

        var detail = await client.GetFromJsonAsync<OrderDetailResponse>(
            OrderByIdUrl(tenantId, order.Id), TestContext.Current.CancellationToken);

        Assert.NotNull(detail?.Client);
        Assert.Equal(("CO", "Juridica", "NIT"),
            (detail.Client.Country, detail.Client.PersonType, detail.Client.IdentificationType));
    }
}
