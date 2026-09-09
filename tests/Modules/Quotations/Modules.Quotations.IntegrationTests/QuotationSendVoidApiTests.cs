using System.Net;
using System.Net.Http.Json;
using Modules.Quotations.Application;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>US-12 (envío, reutilizando la carga de archivos que Storage ya expone -- sin motor
/// de PDF nuevo en el backend) y US-11 (anulación).</summary>
public sealed class QuotationSendVoidApiTests
{
    // `PdfFileId` queda en null a proposito desde que el backend genera el documento: era el
    // `FileResource` que subia el navegador. El PDF ahora vive en `quotation_pdfs`.
    [Fact]
    public async Task SendMarksAsSentAndStampsSentAt()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var quotation = await CreateQuotationAsync(client, tenantId, clientId);

        var response = await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/send",
            new SendQuotationRequest(),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sent = await response.Content.ReadFromJsonAsync<QuotationResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(sent);
        Assert.Equal("Sent", sent.Status);
        Assert.NotNull(sent.SentAt);
    }

    /// <summary>
    /// **El envio no lleva cuerpo**, que es como lo llama el frontend desde que el backend genera
    /// el PDF.
    ///
    /// Esta prueba existe por un defecto real: el endpoint seguia declarando un
    /// `SendQuotationRequest` requerido que no usaba, asi que un `POST` sin cuerpo moria con
    /// `BadHttpRequestException: Implicit body inferred for parameter "request"` y salia como 500.
    /// Ninguna prueba lo agarro porque **todas** mandaban cuerpo -- justo lo que el cliente real
    /// dejo de hacer.
    /// </summary>
    [Fact]
    public async Task SendWorksWithoutABody()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var quotation = await CreateQuotationAsync(client, tenantId, clientId);

        var response = await client.PostAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/send",
            content: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sent = await response.Content.ReadFromJsonAsync<QuotationResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(sent);
        Assert.Equal("Sent", sent.Status);
    }

    // Sin vigencia la cotización nunca vencería (QuotationExpirationProcessor filtra por
    // ValidUntil != null) y quedaría convertible a venta para siempre. El dominio lo corta al
    // salir de Draft; acá se verifica que ese código llega al cliente como 422 y no como 500.
    // Aca vivia `SendWithoutAValidityDateIsUnprocessable`. Se elimino en vez de arreglarse:
    // desde 23ae906 `CreateQuotation` le pone vigencia por defecto, asi que por la API no
    // existe una cotizacion sin `ValidUntil` y su 422 quedo inalcanzable. La invariante sigue
    // viva en `EnsureSendable` y cubierta por `QuotationTests` a nivel de dominio.
    //
    // Enviar exige solo estado y vigencia. Los otros tres requisitos --productos, forma de pago
    // y cuenta de cobro-- son de `EnsureConvertibleToSale`, y se prueban contra ese endpoint.

    // Reemplaza a `SendWithAnUnknownFileIsUnprocessable`, que dejo de tener sentido: enviar ya
    // no recibe un archivo. Lo que esta prueba fija es la compatibilidad -- el frontend todavia
    // manda `pdfFileId` y el backend tiene que ignorarlo, no rechazarlo, o el envio se rompe en
    // cuanto esto se despliega y antes de que el frontend se actualice.
    [Fact]
    public async Task SendIgnoresAPdfFileIdSentByAnOlderFrontend()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var quotation = await CreateQuotationAsync(client, tenantId, clientId);

        var response = await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/send",
            new SendQuotationRequest(Guid.NewGuid()),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // Antes afirmaba lo contrario ("solo Draft puede pasar a Sent"). `Quotation.Send` acepta
    // Sent a proposito --reenviar es el mismo hecho para el agregado, con `SentAt` nuevo-- y el
    // frontend ofrece "Reenviar" desde 0fe2426. Lo que distingue un reenvio de un primer envio
    // es la entrada de historial, no el estado.
    [Fact]
    public async Task SendingAnAlreadySentQuotationResendsIt()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(
            client, factory, tenantId, clientId, productId);

        var response = await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/send",
            new SendQuotationRequest(),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var resent = await response.Content.ReadFromJsonAsync<QuotationResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(resent);
        Assert.Equal("Sent", resent.Status);
        Assert.NotNull(resent.SentAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VoidWorksFromDraftOrSent(bool sendFirst)
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var quotation = await CreateQuotationAsync(client, tenantId, clientId);
        if (sendFirst)
        {
            var pdfFileId = await CreateAvailablePdfFileAsync(client, factory, tenantId);
            await client.PostAsJsonAsync(
                $"{QuotationsUrl(tenantId)}/{quotation.Id}/send",
                new SendQuotationRequest(pdfFileId),
                TestContext.Current.CancellationToken);
        }

        var response = await client.PostAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/void",
            content: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var voided = await response.Content.ReadFromJsonAsync<QuotationResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(voided);
        Assert.Equal("Voided", voided.Status);
    }

    [Fact]
    public async Task VoidingAnAlreadyVoidedQuotationIsUnprocessable()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var quotation = await CreateQuotationAsync(client, tenantId, clientId);
        await client.PostAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/void",
            content: null,
            TestContext.Current.CancellationToken);

        var response = await client.PostAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/void",
            content: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    // US-11: una cotización anulada "queda de sólo lectura".
    [Fact]
    public async Task EditingAVoidedQuotationIsUnprocessable()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var quotation = await CreateQuotationAsync(client, tenantId, clientId);
        await client.PostAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/void",
            content: null,
            TestContext.Current.CancellationToken);

        var response = await client.PatchAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}",
            new UpdateQuotationRequest(null, "Efectivo", null, null, null),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    // US-10: editar en Sent sigue permitido -- sólo Voided/Expired bloquean.
    [Fact]
    public async Task AddingAnItemToASentQuotationIsAllowed()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateQuotationAsync(client, tenantId, clientId);
        var pdfFileId = await CreateAvailablePdfFileAsync(client, factory, tenantId);
        await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/send",
            new SendQuotationRequest(pdfFileId),
            TestContext.Current.CancellationToken);

        var response = await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/items",
            new AddQuotationItemRequest(productId, 1m),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task VoidForAnotherTenantIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, owner) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = owner;
        var clientId = await CreateActiveCustomerAsync(owner, tenantId);
        var quotation = await CreateQuotationAsync(owner, tenantId, clientId);

        var (_, _, otherOwner) = await RegisterTenantAsync(
            factory, QuotationsPermissions.QuotationManage);
        using var __ = otherOwner;

        var response = await otherOwner.PostAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/void",
            content: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>Las extensiones de ProblemDetails llegan aplanadas en la raíz
    /// (ApiExceptionHandler).</summary>
    private sealed record ProblemPayload(string Code);
}
