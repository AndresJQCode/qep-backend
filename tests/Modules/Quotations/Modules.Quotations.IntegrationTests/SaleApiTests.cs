using System.Net;
using System.Net.Http.Json;
using Modules.Quotations.Application;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>US-13 a US-17: conversión de una cotización en venta. Ya no hace falta haberla
/// enviado (a pedido, 2026-09).</summary>
public sealed class SaleApiTests
{
    private static string SaleUrl(Guid tenantId, Guid quotationId) =>
        $"{QuotationsUrl(tenantId)}/{quotationId}/sale";

    // Bug real, 2026-09-12: el editor de cotizaciones dejo de pedir la forma de pago hace
    // rato (ver `UpdateQuotationRequest` en el frontend), asi que toda cotizacion nueva la
    // tiene en null -- pero `EnsureConvertibleToSale` seguia exigiendola, y con eso "Convertir
    // en venta" no aparecia nunca para nadie. La cobertura vieja probaba lo contrario (que
    // rechazara sin forma de pago); esta prueba el arreglo.
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
            SaleUrl(tenantId, quotation.Id),
            new ConvertQuotationToSaleRequest(
                "FullPaymentReceived",
                "Pago verificado",
                [new SalePaymentProofRequest(proofFileId, quotation.Total)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task ConvertCreatesTheSaleAndLeavesTheQuotationSent()
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
            SaleUrl(tenantId, quotation.Id),
            new ConvertQuotationToSaleRequest(
                "FullPaymentReceived", "Pago verificado", [new SalePaymentProofRequest(proofFileId, quotation.Total)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var sale = await response.Content.ReadFromJsonAsync<SaleResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(sale);
        // Nace Pending desde aa020a8: convertir dejo de ser aprobar. Quien convierte y quien da
        // el visto bueno son roles distintos, y el estado es lo que hace visible ese paso.
        Assert.Equal("Pending", sale.Status);
        Assert.Equal("FullPaymentReceived", sale.PaymentStatus);
        Assert.Equal(quotation.Id, sale.QuotationId);
        Assert.StartsWith(
            $"VEN-{DateTime.UtcNow.Year}-", sale.SaleNumber, StringComparison.Ordinal);
        Assert.Null(sale.RitualCollectionSyncId);
        var proof = Assert.Single(sale.PaymentProofs);
        Assert.Equal(proofFileId, proof.FileId);
        Assert.Equal(quotation.Total, proof.Amount);

        // No hay estado "aprobada": convertir a venta deja la cotizacion en Sent -- la Sale
        // creada, referenciando este QuotationId, es la unica senal de que ya se convirtio.
        var fetchedQuotation = await client.GetFromJsonAsync<QuotationResponse>(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}", TestContext.Current.CancellationToken);
        Assert.NotNull(fetchedQuotation);
        Assert.Equal("Sent", fetchedQuotation.Status);
    }

    // Como la cotizacion se queda en Sent despues de convertirse (no hay estado "aprobada" que
    // bloquee una segunda conversion), la unica red es el indice unico de Sale.QuotationId --
    // esta prueba confirma que un segundo intento da un 422 legible, no un 500 con el nombre de
    // la constraint adentro.
    [Fact]
    public async Task ConvertingAnAlreadyConvertedQuotationIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var firstProofFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        (await client.PostAsJsonAsync(
            SaleUrl(tenantId, quotation.Id),
            new ConvertQuotationToSaleRequest(
                "FullPaymentReceived", null, [new SalePaymentProofRequest(firstProofFileId, quotation.Total)]),
            TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        var secondProofFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var response = await client.PostAsJsonAsync(
            SaleUrl(tenantId, quotation.Id),
            new ConvertQuotationToSaleRequest(
                "FullPaymentReceived", null, [new SalePaymentProofRequest(secondProofFileId, quotation.Total)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("quotation.quotation.already_converted", body, StringComparison.Ordinal);
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
            SaleUrl(tenantId, quotation.Id),
            new ConvertQuotationToSaleRequest("PaymentPending", null, []),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var sale = await response.Content.ReadFromJsonAsync<SaleResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(sale);
        Assert.Empty(sale.PaymentProofs);
    }

    [Fact]
    public async Task ConvertWithoutProofsWhenPaymentIsNotPendingIsUnprocessable()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);

        var response = await client.PostAsJsonAsync(
            SaleUrl(tenantId, quotation.Id),
            new ConvertQuotationToSaleRequest("PartialPaymentReceived", null, []),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
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
            SaleUrl(tenantId, quotation.Id),
            new ConvertQuotationToSaleRequest("PaymentPending", null, []),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task ConvertWithAnInvalidPaymentStatusIsUnprocessable()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);

        var response = await client.PostAsJsonAsync(
            SaleUrl(tenantId, quotation.Id),
            new ConvertQuotationToSaleRequest("NotAStatus", null, []),
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
            SaleUrl(tenantId, quotation.Id),
            new ConvertQuotationToSaleRequest(
                "FullPaymentReceived", null, [new SalePaymentProofRequest(proofFileId, 0m)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task GetSaleReturnsTheConvertedSale()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var proofFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var created = await (await client.PostAsJsonAsync(
            SaleUrl(tenantId, quotation.Id),
            new ConvertQuotationToSaleRequest(
                "FullPaymentReceived", null, [new SalePaymentProofRequest(proofFileId, quotation.Total)]),
            TestContext.Current.CancellationToken))
            .Content.ReadFromJsonAsync<SaleResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(created);

        var response = await client.GetAsync(
            SaleUrl(tenantId, quotation.Id), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var fetched = await response.Content.ReadFromJsonAsync<SaleResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(fetched);
        Assert.Equal(created.Id, fetched.Id);
        Assert.Equal(created.SaleNumber, fetched.SaleNumber);
    }

    [Fact]
    public async Task GetSaleForAnUnconvertedQuotationReturnsNotFound()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var quotation = await CreateQuotationAsync(client, tenantId, clientId);

        var response = await client.GetAsync(
            SaleUrl(tenantId, quotation.Id), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ConvertForAnotherTenantIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, owner) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = owner;
        var clientId = await CreateActiveCustomerAsync(owner, tenantId);
        var productId = await CreateProductWithScalesAsync(owner, tenantId);
        var quotation = await CreateSentQuotationAsync(owner, factory, tenantId, clientId, productId);

        var (_, _, otherOwner) = await RegisterTenantAsync(factory, SalesPermissions.SaleManage);
        using var __ = otherOwner;

        var response = await otherOwner.PostAsJsonAsync(
            SaleUrl(tenantId, quotation.Id),
            new ConvertQuotationToSaleRequest("PaymentPending", null, []),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // Lo que se convierte en venta es lo que el cliente recibio. Editar una enviada sigue
    // permitido -- sigue siendo editable --, pero deja la cotizacion y el PDF entregado
    // diciendo cosas distintas: convertir ahi adentro registraria una venta por importes que
    // nadie le mando. La salida es reenviarla.
    [Fact]
    public async Task ConvertAQuotationEditedAfterBeingSentIsUnprocessable()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        await ChangeTheQuantityAsync(client, tenantId, quotation);

        var response = await client.PostAsJsonAsync(
            SaleUrl(tenantId, quotation.Id),
            new ConvertQuotationToSaleRequest("PaymentPending", null, []),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(problem);
        Assert.Equal("quotation.quotation.changed_since_sent", problem.Code);

        // Y la cotizacion lo dice al leerla: de esos dos campos sale el boton apagado y el
        // aviso que explica por que, sin que la pantalla compare fechas por su cuenta.
        var fetched = await client.GetFromJsonAsync<QuotationResponse>(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}", TestContext.Current.CancellationToken);
        Assert.NotNull(fetched);
        Assert.True(fetched.HasChangesSinceSent);
        Assert.False(fetched.CanBeConvertedToSale);
    }

    [Fact]
    public async Task ResendingMakesAnEditedQuotationConvertibleAgain()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        await ChangeTheQuantityAsync(client, tenantId, quotation);

        var pdfFileId = await CreateAvailablePdfFileAsync(client, factory, tenantId);
        var resent = await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/send",
            new SendQuotationRequest(pdfFileId),
            TestContext.Current.CancellationToken);
        resent.EnsureSuccessStatusCode();
        var afterResend = await resent.Content.ReadFromJsonAsync<QuotationResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(afterResend);
        Assert.False(afterResend.HasChangesSinceSent);
        Assert.True(afterResend.CanBeConvertedToSale);

        var response = await client.PostAsJsonAsync(
            SaleUrl(tenantId, quotation.Id),
            new ConvertQuotationToSaleRequest("PaymentPending", null, []),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    /// <summary>Una edicion cualquiera sobre la cotizacion ya enviada: mueve UpdatedAt por
    /// delante de SentAt, que es lo unico que define "cambio despues de enviarse".</summary>
    private static async Task ChangeTheQuantityAsync(
        HttpClient client, Guid tenantId, QuotationResponse quotation)
    {
        var itemId = Assert.Single(quotation.Items).Id;
        var response = await client.PutAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/items/{itemId}",
            new UpdateQuotationItemRequest(2m),
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private sealed record ProblemPayload(string Code);
}
