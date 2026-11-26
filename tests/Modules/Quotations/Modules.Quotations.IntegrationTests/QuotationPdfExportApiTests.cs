using System.Net;
using System.Net.Http.Json;
using Modules.Quotations.Application;
using Modules.Tenancy.Application;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// El PDF de la cotización, de punta a punta con el logo del tenant (spec 2026-09-19): exportar,
/// asignar un logo por Tenancy, y volver a exportar. `QuotationExportApiTests.cs` en esta misma
/// carpeta prueba la exportación **asíncrona a Excel** (`POST /export`), un endpoint distinto sin
/// relación con este — este archivo es el primero que ejercita `POST /{quotationId}/pdf` por
/// integración (antes sólo tenía cobertura unitaria, `ExportQuotationPdfHandlerTests`).
/// </summary>
public sealed class QuotationPdfExportApiTests
{
    [Fact]
    public async Task ExportAfterAssigningALogoRegenerates()
    {
        await using var database = await StartDatabaseAsync();
        // `publicPaymentProofLinks: true` -- el mismo interruptor que usan los comprobantes
        // públicos configura acá el bucket público del logo (`FilePublication.PublishAsync`
        // rechaza con `storage.public.not_configured` si `IPublicObjectStorage.IsConfigured` es
        // `false`, que es el default de esta factoría).
        using var factory = new QepApiFactory(database.GetConnectionString(), publicPaymentProofLinks: true);
        var (tenantId, _, client) = await RegisterTenantAsync(
            factory, [.. ManagerPermissions, TenancyPermissions.SettingsRead, TenancyPermissions.SettingsUpdate]);
        using var _ = client;
        var customerId = await CreateActiveCustomerAsync(client, tenantId);
        var quotation = await CreateQuotationAsync(client, tenantId, customerId);

        var firstExport = await client.PostAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/pdf", content: null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, firstExport.StatusCode);
        var first = await firstExport.Content.ReadFromJsonAsync<ExportDto>(TestContext.Current.CancellationToken);

        var fileId = await UploadAndCompleteTenantFileAsync(client, tenantId, factory);
        var etag = await GetSettingsEtagAsync(client, tenantId);
        var putLogo = await client.SendAsync(
            PutLogoRequest(tenantId, etag, fileId), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, putLogo.StatusCode);

        var secondExport = await client.PostAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/pdf", content: null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, secondExport.StatusCode);
        var second = await secondExport.Content.ReadFromJsonAsync<ExportDto>(TestContext.Current.CancellationToken);

        Assert.NotEqual(first!.GeneratedAt, second!.GeneratedAt);
    }

    // Spec 2026-10-23 D-R3: el documento lleva los separadores del formato numérico del tenant, y cambiar el formato
    // invalida el PDF cacheado (la clave de caché incluye `NumberFormat`).
    [Fact]
    public async Task TheDocumentUsesTheTenantSeparatorsAndRegeneratesWhenTheyChange()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var customerId = await CreateActiveCustomerAsync(client, tenantId);
        var quotation = await CreateQuotationAsync(client, tenantId, customerId);

        var first = await ExportAsync(client, tenantId, quotation.Id);
        var firstDocument = factory.PdfRenderer.LastDocument;
        Assert.NotNull(firstDocument);
        Assert.Equal((".", ","), (firstDocument.ThousandsSeparator, firstDocument.DecimalSeparator));

        await SetTenantRegionalFormatAsync(factory, tenantId, numberFormat: "1,234.56");

        var second = await ExportAsync(client, tenantId, quotation.Id);
        var secondDocument = factory.PdfRenderer.LastDocument;
        Assert.NotNull(secondDocument);
        Assert.NotEqual(first.GeneratedAt, second.GeneratedAt);
        Assert.Equal((",", "."), (secondDocument.ThousandsSeparator, secondDocument.DecimalSeparator));

        var third = await ExportAsync(client, tenantId, quotation.Id);
        Assert.Equal(second.GeneratedAt, third.GeneratedAt);
    }

    // Spec 2026-11-24 §7: el documento que recibe el renderizador lleva el código postal de la entrega en la ubicación,
    // con la dirección en Colombia y antes de la ciudad y el país fuera. Guardar sube la versión, así que se regenera.
    [Fact]
    public async Task TheDocumentPrintsTheShippingPostalCodeInTheLocation()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var customerId = await CreateActiveCustomerAsync(client, tenantId);
        var home = (await EnsureCityIdsInTwoDepartmentsAsync(client)).First;
        var colombian = new QuotationPartyRequest(
            "Bodega", null, "3001234567", "bodega@x.co", "Zona Franca", home.DepartmentId, home.CityId,
            PostalCode: "050021");
        var quotation = await CreateQuotationAsync(
            client, tenantId, customerId, parties: new QuotationPartiesRequest(null, colombian));

        await ExportAsync(client, tenantId, quotation.Id);
        Assert.Equal("Zona Franca, 050021", factory.PdfRenderer.LastDocument!.Shipping.Location);

        var spanish = new QuotationPartyRequest(
            "Bodega", null, "+34600111333", "bodega@madrid.es", "Calle Alcalá 20", null, null,
            Country: "ES", CityName: "Madrid", PostalCode: "28014");
        using var put = new HttpRequestMessage(HttpMethod.Put, $"{QuotationsUrl(tenantId)}/{quotation.Id}")
        {
            Content = JsonContent.Create(new SaveQuotationRequest(
                quotation.ValidUntil, null, null, new QuotationPartiesRequest(null, spanish), null, null)),
        };
        put.Headers.TryAddWithoutValidation("If-Match", $"\"{quotation.Version}\"");
        var saved = await client.SendAsync(put, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        await ExportAsync(client, tenantId, quotation.Id);
        Assert.Equal("Calle Alcalá 20, 28014, Madrid, ES", factory.PdfRenderer.LastDocument!.Shipping.Location);
    }

    // Spec impuestos por tipo §9.3 (F-1): el documento que recibe el renderizador trae una fila por retención que
    // aplicó, con el nombre de la tasa; la que no alcanza su base mínima no sale.
    [Fact]
    public async Task TheDocumentCarriesOneRowPerAppliedWithholdingWithTheRateName()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        // Una UVT redonda: las dos tasas (4 UVT = 200.000) aplican sobre una base de 300.000. La Retefuente es del
        // cliente; la ReteICA, de la empresa en su municipio, donde el cliente es agente (spec ReteICA por empresa §7.5).
        await InsertTaxUnitValueAsync(factory, "CO", TodayInBogota().Year, 50_000m);
        var cityId = await EnsureCityIdAsync(client);
        var company = await CreateCompanyWithBankAccountAsync(client, tenantId, cityId: cityId);
        var vat = await CreateTaxRateAsync(client, tenantId, "IVA 19%", 19);
        var productId = await CreateProductWithScalesAsync(client, tenantId, baseCop: 119_000m, taxRateId: vat);
        var retefuente = await InsertWithholdingRateAsync(factory, tenantId, "Retefuente 2,5 %", "Retefuente", 2.5m, minimumBase: 4m);
        var reteIca = await InsertWithholdingRateAsync(
            factory, tenantId, "ReteICA Bogotá", "ReteICA", 0.966m, minimumBase: 4m, cityId: cityId);
        await AssignCompanyWithholdingRateAsync(factory, tenantId, company.CompanyId, reteIca);
        var customerId = await CreateActiveCustomerAsync(client, tenantId, withholdingTaxRateIds: [retefuente]);
        await SetWithholdingAgentAsync(factory, tenantId, customerId, cityId);
        var quotation = await CreateQuotationAsync(
            client, tenantId, customerId,
            billingAccount: new QuotationBillingAccountRequest(
                company.CompanyId, company.BankName, company.AccountNumber, company.Currency));
        var withItems = await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/items",
            new AddQuotationItemRequest(productId, 3m),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, withItems.StatusCode);

        await ExportAsync(client, tenantId, quotation.Id);

        var document = factory.PdfRenderer.LastDocument;
        Assert.NotNull(document);
        // Cada fila con su porcentaje efectivo, con el separador del tenant (spec ReteICA por empresa §7.9).
        Assert.Equal(
            new[]
            {
                new QuotationPdfWithholding("Retefuente 2,5 %", 7_500m, "2,5 %"),
                new QuotationPdfWithholding("ReteICA Bogotá", 2_898m, "0,966 %"),
            },
            document.Withholdings);
        Assert.Equal(10_398m, document.RetentionAmount);
    }

    [Fact]
    public async Task TheDocumentOfAQuotationWithoutWithholdingsHasNoRetentionRow()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var customerId = await CreateActiveCustomerAsync(client, tenantId);
        var quotation = await CreateQuotationAsync(client, tenantId, customerId);

        await ExportAsync(client, tenantId, quotation.Id);

        Assert.Empty(factory.PdfRenderer.LastDocument!.Withholdings);
    }

    private static async Task<ExportDto> ExportAsync(HttpClient client, Guid tenantId, Guid quotationId)
    {
        var response = await client.PostAsync(
            $"{QuotationsUrl(tenantId)}/{quotationId}/pdf", content: null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var export = await response.Content.ReadFromJsonAsync<ExportDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(export);
        return export;
    }

    // Spec 2026-10-08: the document carries the catalogue symbol and decimals of the quotation
    // currency (the billing account's), so the template stops hardcoding "$ " and COP decimals.
    [Fact]
    public async Task TheDocumentCarriesTheSymbolAndDecimalsOfTheQuotationCurrency()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var customerId = await CreateActiveCustomerAsync(client, tenantId);
        var billing = await CreateCompanyWithBankAccountAsync(client, tenantId, currency: "EUR");
        var quotation = await CreateQuotationAsync(
            client,
            tenantId,
            customerId,
            validUntil: null,
            billingAccount: new QuotationBillingAccountRequest(
                billing.CompanyId, billing.BankName, billing.AccountNumber, billing.Currency));

        var export = await client.PostAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/pdf", content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        var document = factory.PdfRenderer.LastDocument;
        Assert.NotNull(document);
        Assert.Equal(("EUR", "€", 2), (document.Currency, document.Symbol, document.Decimals));
    }

    /// <summary>Sube el logo del tenant (mismo pipeline de Storage que ya ejerce
    /// <c>CreateAvailableFileAsync</c>, pero con <c>ownerId == tenantId</c>: <c>TenantLogoStorage</c>
    /// exige esa igualdad para <c>FileOwnerType.Tenant</c>, si no responde
    /// <c>tenancy.logo.not_owned</c>). El contenido es un PNG real: uno de ceros llega quarantined
    /// por el decode de ImageSharp en <c>CompleteUploadHandler</c> (mismo hallazgo que
    /// <c>TenantLogoApiTests</c>).</summary>
    private static async Task<Guid> UploadAndCompleteTenantFileAsync(
        HttpClient client, Guid tenantId, QepApiFactory factory)
    {
        using var image = new Image<Rgba32>(16, 16, Color.CornflowerBlue);
        await using var png = new MemoryStream();
        await image.SaveAsPngAsync(png, TestContext.Current.CancellationToken);
        var content = png.ToArray();

        var sessionResponse = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/files",
            new
            {
                ownerId = tenantId,
                ownerType = "Tenant",
                name = "logo.png",
                mimeType = "image/png",
                sizeBytes = content.Length,
            },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, sessionResponse.StatusCode);
        var session = (await sessionResponse.Content.ReadFromJsonAsync<UploadSessionResponseDto>(
            TestContext.Current.CancellationToken))!;

        factory.ObjectStorage.Upload(session.StorageKey, content);

        var completeResponse = await client.PostAsync(
            $"/api/v1/tenants/{tenantId}/files/{session.FileResourceId}/complete",
            content: null,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, completeResponse.StatusCode);

        return session.FileResourceId;
    }

    private static async Task<string> GetSettingsEtagAsync(HttpClient client, Guid tenantId)
    {
        var response = await client.GetAsync(
            $"/api/v1/tenants/{tenantId}/settings", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return response.Headers.ETag!.Tag;
    }

    private static HttpRequestMessage PutLogoRequest(Guid tenantId, string etag, Guid fileId)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Put, $"/api/v1/tenants/{tenantId}/settings/logo")
        {
            Content = JsonContent.Create(new { fileId }),
        };
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        return request;
    }

    private sealed record ExportDto(string Url, DateTimeOffset GeneratedAt);
}
