using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Quotations.Application;
using Modules.Quotations.Infrastructure.Persistence;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// Guardar una cotización editable de una vez (<c>PUT /quotations/{quotationId}</c> con
/// <c>If-Match</c>) y su cálculo previo (<c>POST /quotations/{quotationId}/preview</c>), contra
/// Postgres real. Gemelo de <see cref="OrderEditsApiTests"/> por el lado de la cotización.
/// </summary>
public sealed class QuotationEditsApiTests
{
    private static string QuotationUrl(Guid tenantId, Guid quotationId) =>
        $"{QuotationsUrl(tenantId)}/{quotationId}";

    // La versión viaja al frontend para mandarla en If-Match, y el PUT deja la nueva en el ETag.
    [Fact]
    public async Task TheQuotationResponseCarriesTheVersionAndTheSaveSetsTheETag()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var (quotation, productId) = await CreateEditableQuotationAsync(client, factory, tenantId);
        Assert.True(quotation.Version >= 1);

        var response = await PutEditsAsync(
            client, tenantId, quotation.Id, quotation.Version,
            RequestFor(quotation, notes: "Entregar el lunes", items: [new QuotationEditItemRequest(productId, 1m)]));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = await ReadQuotationAsync(response);
        Assert.True(saved.Version > quotation.Version);
        Assert.Equal($"\"{saved.Version}\"", response.Headers.ETag?.Tag);
        Assert.Equal(saved.Version, (await GetQuotationAsync(client, tenantId, quotation.Id)).Version);
    }

    // Encabezado + altas, cambios de cantidad y bajas en una sola escritura, y la relectura los
    // muestra todos.
    [Fact]
    public async Task SaveAppliesTheHeaderAndTheItemDiffInOneRequest()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var (quotation, keptProductId) = await CreateEditableQuotationAsync(client, factory, tenantId);
        var removedProductId = await CreateProductWithScalesAsync(client, tenantId, baseCop: 50_000m);
        var addedProductId = await CreateProductWithScalesAsync(client, tenantId, baseCop: 50_000m);
        var withTwoLines = await AddItemAsync(client, tenantId, quotation.Id, removedProductId, 1m);
        var validUntil = TodayInBogota().AddDays(45);

        var response = await PutEditsAsync(
            client, tenantId, quotation.Id, withTwoLines.Version,
            RequestFor(
                withTwoLines,
                validUntil: validUntil,
                paymentMethod: "Contado",
                notes: "Entregar el lunes",
                items:
                [
                    new QuotationEditItemRequest(keptProductId, 3m),
                    new QuotationEditItemRequest(addedProductId, 2m),
                ]));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = await ReadQuotationAsync(response);
        AssertDocument(saved);

        AssertDocument(await GetQuotationAsync(client, tenantId, quotation.Id));

        var auditActions = (await OutboxMessagesAsync(factory, "platform.audit.recorded.v1"))
            .Select(ActionOf)
            .ToArray();
        Assert.Contains("quotation.quotation.updated", auditActions);
        Assert.Contains("quotation.quotation.item_added", auditActions);
        Assert.Contains("quotation.quotation.item_updated", auditActions);
        Assert.Contains("quotation.quotation.item_removed", auditActions);

        void AssertDocument(QuotationResponse document)
        {
            Assert.Equal(validUntil, document.ValidUntil);
            Assert.Equal("Contado", document.PaymentMethod);
            Assert.Equal("Entregar el lunes", document.Notes);
            Assert.Equal(2, document.Items.Count);
            Assert.Equal(3m, Assert.Single(document.Items, item => item.ProductId == keptProductId).Quantity);
            Assert.Equal(2m, Assert.Single(document.Items, item => item.ProductId == addedProductId).Quantity);
            Assert.DoesNotContain(document.Items, item => item.ProductId == removedProductId);
            Assert.Equal(400_000m, document.Total);
        }
    }

    // Mismo contrato de precondición que PUT /orders/{orderId}: sin If-Match 428, con una versión
    // vieja 412 — y en los dos casos no se guardó nada.
    [Fact]
    public async Task SaveRequiresAFreshIfMatch()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var (quotation, productId) = await CreateEditableQuotationAsync(client, factory, tenantId);
        var body = RequestFor(quotation, notes: "Primera", items: [new QuotationEditItemRequest(productId, 1m)]);

        var first = await PutEditsAsync(client, tenantId, quotation.Id, quotation.Version, body);
        first.EnsureSuccessStatusCode();

        var stale = await PutEditsAsync(
            client, tenantId, quotation.Id, quotation.Version, body with { Notes = "Segunda" });
        var missing = await PutEditsAsync(
            client, tenantId, quotation.Id, null, body with { Notes = "Tercera" });

        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal("concurrency.conflict", (await ReadProblemAsync(stale)).Code);
        Assert.Equal(HttpStatusCode.PreconditionRequired, missing.StatusCode);
        Assert.Equal("precondition.if_match_required", (await ReadProblemAsync(missing)).Code);
        Assert.Equal("Primera", (await GetQuotationAsync(client, tenantId, quotation.Id)).Notes);
    }

    // Una cotización convertida se edita por PUT /orders/{orderId}: acá se rechaza con el código
    // del dominio (Quotation.EnsureEditable), no se edita en silencio.
    [Fact]
    public async Task SaveOnAConvertedQuotationIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var (quotation, productId) = await CreateEditableQuotationAsync(client, factory, tenantId);
        var converted = await client.PostAsJsonAsync(
            $"{QuotationUrl(tenantId, quotation.Id)}/order",
            new ConvertQuotationToOrderRequest("PaymentPending", null, []),
            TestContext.Current.CancellationToken);
        converted.EnsureSuccessStatusCode();
        var reloaded = await GetQuotationAsync(client, tenantId, quotation.Id);

        var save = await PutEditsAsync(
            client, tenantId, quotation.Id, reloaded.Version,
            RequestFor(reloaded, items: [new QuotationEditItemRequest(productId, 5m)]));
        var preview = await PreviewEditsAsync(
            client, tenantId, quotation.Id,
            RequestFor(reloaded, items: [new QuotationEditItemRequest(productId, 5m)]));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, save.StatusCode);
        Assert.Equal("quotation.quotation.not_editable", (await ReadProblemAsync(save)).Code);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, preview.StatusCode);
        Assert.Equal("quotation.quotation.not_editable", (await ReadProblemAsync(preview)).Code);
    }

    // El cálculo previo devuelve el documento recalculado y una relectura prueba que no quedó nada
    // guardado: ni líneas, ni encabezado, ni versión, ni auditoría.
    [Fact]
    public async Task PreviewReturnsTheRecalculatedDocumentWithoutPersistingIt()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var (quotation, productId) = await CreateEditableQuotationAsync(client, factory, tenantId);
        var auditBefore = (await OutboxMessagesAsync(factory, "platform.audit.recorded.v1")).Count;

        var response = await PreviewEditsAsync(
            client, tenantId, quotation.Id,
            RequestFor(quotation, notes: "Borrador", items: [new QuotationEditItemRequest(productId, 3m)]));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var preview = await ReadQuotationAsync(response);
        Assert.Equal(3m, Assert.Single(preview.Items).Quantity);
        Assert.Equal(300_000m, preview.Total);
        Assert.Equal("Borrador", preview.Notes);
        // La versión que vuelve es la guardada: la pantalla nunca debe mandar en If-Match una que
        // sólo existió en este cálculo.
        Assert.Equal(quotation.Version, preview.Version);

        var fetched = await GetQuotationAsync(client, tenantId, quotation.Id);
        Assert.Equal(1m, Assert.Single(fetched.Items).Quantity);
        Assert.Equal(quotation.Total, fetched.Total);
        Assert.Null(fetched.Notes);
        Assert.Equal(quotation.Version, fetched.Version);
        Assert.Equal(auditBefore, (await OutboxMessagesAsync(factory, "platform.audit.recorded.v1")).Count);
    }

    [Fact]
    public async Task PreviewReportsTheSameDomainErrorAsSaving()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var (quotation, productId) = await CreateEditableQuotationAsync(client, factory, tenantId);
        var body = RequestFor(
            quotation,
            items:
            [
                new QuotationEditItemRequest(productId, 1m),
                new QuotationEditItemRequest(Guid.CreateVersion7(), 1m),
            ]);

        var preview = await PreviewEditsAsync(client, tenantId, quotation.Id, body);
        var save = await PutEditsAsync(client, tenantId, quotation.Id, quotation.Version, body);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, preview.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, save.StatusCode);
        Assert.Equal("quotation.item.product_not_found", (await ReadProblemAsync(preview)).Code);
        Assert.Equal("quotation.item.product_not_found", (await ReadProblemAsync(save)).Code);
    }

    [Fact]
    public async Task AnUnknownQuotationIsNotFound()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var (quotation, productId) = await CreateEditableQuotationAsync(client, factory, tenantId);
        var unknownId = Guid.CreateVersion7();
        var body = RequestFor(quotation, items: [new QuotationEditItemRequest(productId, 1m)]);

        var preview = await PreviewEditsAsync(client, tenantId, unknownId, body);
        var save = await PutEditsAsync(client, tenantId, unknownId, 1, body);

        Assert.Equal(HttpStatusCode.NotFound, preview.StatusCode);
        Assert.Equal("quotation.quotation.not_found", (await ReadProblemAsync(preview)).Code);
        Assert.Equal(HttpStatusCode.NotFound, save.StatusCode);
        Assert.Equal("quotation.quotation.not_found", (await ReadProblemAsync(save)).Code);
    }

    // US-6/US-10: el guardado reemplaza el encabezado entero, incluidas las sobrescrituras de
    // facturacion/entrega, y sube la version (concurrencia optimista).
    [Fact]
    public async Task UpdateReplacesTheEditableHeaderFields()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var created = await CreateQuotationAsync(client, tenantId, clientId);

        var validUntil = TodayInBogota().AddDays(30);
        var response = await PutEditsAsync(
            client, tenantId, created.Id, created.Version,
            RequestFor(
                created,
                validUntil: validUntil,
                paymentMethod: "Efectivo",
                notes: "Nota de prueba",
                parties: new QuotationPartiesRequest(
                    new QuotationPartyRequest("Nombre alterno", null, null, null, null, null),
                    Shipping: null)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await ReadQuotationAsync(response);
        Assert.Equal(validUntil, updated.ValidUntil);
        Assert.Equal("Efectivo", updated.PaymentMethod);
        Assert.Equal("Nota de prueba", updated.Notes);
        var billing = Assert.Single(updated.Parties);
        Assert.Equal("Billing", billing.Role);
        Assert.Equal("Nombre alterno", billing.Name);
        Assert.NotEqual(created.UpdatedAt, updated.UpdatedAt);
        Assert.False(updated.IsStorePickup);
    }

    // Recoger en tienda gana sobre una parte de entrega que venga en el mismo guardado, borra la
    // que ya estaba guardada, no toca la facturacion, y sobrevive a la ida y vuelta por la base.
    [Fact]
    public async Task UpdateWithStorePickupDropsTheShippingPartyAndPersists()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var created = await CreateQuotationAsync(client, tenantId, clientId);
        Assert.False(created.IsStorePickup);

        var billing = new QuotationPartyRequest("Sede administrativa", null, null, null, null, null);
        var shipping = new QuotationPartyRequest("Bodega Fontibon", null, null, "Zona Franca", null, null);

        var withShippingResponse = await PutEditsAsync(
            client, tenantId, created.Id, created.Version,
            RequestFor(
                created, paymentMethod: "Efectivo",
                parties: new QuotationPartiesRequest(billing, shipping)));
        Assert.Equal(HttpStatusCode.OK, withShippingResponse.StatusCode);
        var withShipping = await ReadQuotationAsync(withShippingResponse);

        var response = await PutEditsAsync(
            client, tenantId, created.Id, withShipping.Version,
            RequestFor(
                created, paymentMethod: "Efectivo",
                parties: new QuotationPartiesRequest(billing, shipping, IsStorePickup: true)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await ReadQuotationAsync(response);
        Assert.True(updated.IsStorePickup);
        var onlyBilling = Assert.Single(updated.Parties);
        Assert.Equal("Billing", onlyBilling.Role);

        var fetched = await GetQuotationAsync(client, tenantId, created.Id);
        Assert.True(fetched.IsStorePickup);
        Assert.DoesNotContain(fetched.Parties, party => party.Role == "Shipping");
    }

    // Facturar a consumidor final: sin parte propia, con el IVA cobrado y sin retencion aunque el
    // cliente tenga las dos cosas. Sobrevive a la ida y vuelta por la base, y desmarcarlo en el
    // siguiente guardado devuelve la retencion y el excedente del cliente sin volver a crearlo.
    [Fact]
    public async Task UpdateBillingToTheFinalConsumerDropsRetentionAndVatSurplusAndPersists()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(
            client, tenantId, withRetention: true, vatSurplus: true);
        var taxRateId = await CreateTaxRateAsync(client, tenantId, "IVA 19%", 19);
        // 119_000 con el IVA del 19% ya adentro: base 100_000 e IVA 19_000 redondos.
        var productId = await CreateProductWithScalesAsync(
            client, tenantId, baseCop: 119_000m, taxRateId: taxRateId);
        var created = await CreateQuotationAsync(client, tenantId, clientId);
        var added = await client.PostAsJsonAsync(
            $"{QuotationUrl(tenantId, created.Id)}/items",
            new AddQuotationItemRequest(productId, 1m),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        var withItem = await ReadQuotationAsync(added);

        var response = await PutEditsAsync(
            client, tenantId, created.Id, withItem.Version,
            RequestFor(
                withItem,
                paymentMethod: "Efectivo",
                parties: new QuotationPartiesRequest(null, null, BillsToFinalConsumer: true),
                items: [new QuotationEditItemRequest(productId, 1m)]));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await ReadQuotationAsync(response);
        Assert.True(updated.BillsToFinalConsumer);
        Assert.False(updated.IsStorePickup);
        Assert.Empty(updated.Parties);
        Assert.False(updated.CustomerVatSurplus);
        Assert.Equal(19_000m, updated.TaxAmount);
        Assert.Equal(119_000m, updated.Total);
        Assert.Equal(0m, updated.RetentionAmount);
        Assert.Equal(119_000m, updated.NetTotal);

        var fetched = await GetQuotationAsync(client, tenantId, created.Id);
        Assert.True(fetched.BillsToFinalConsumer);
        Assert.Equal(0m, fetched.RetentionAmount);
        Assert.Equal(19_000m, fetched.TaxAmount);

        var unmarked = await PutEditsAsync(
            client, tenantId, created.Id, updated.Version,
            RequestFor(
                updated,
                paymentMethod: "Efectivo",
                parties: new QuotationPartiesRequest(null, null),
                items: [new QuotationEditItemRequest(productId, 1m)]));

        Assert.Equal(HttpStatusCode.OK, unmarked.StatusCode);
        var restored = await ReadQuotationAsync(unmarked);
        Assert.False(restored.BillsToFinalConsumer);
        Assert.True(restored.CustomerVatSurplus);
        Assert.Equal(0m, restored.TaxAmount);
        Assert.Equal(100_000m, restored.Total);
        Assert.Equal(2_500m, restored.RetentionAmount);
        Assert.Equal(97_500m, restored.NetTotal);
    }

    // Consumidor final con datos propios de facturacion son dos nombres para la misma factura: el
    // dominio lo rechaza con su codigo, no elige uno.
    [Fact]
    public async Task UpdateBillingToTheFinalConsumerWithABillingPartyIsUnprocessable()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var created = await CreateQuotationAsync(client, tenantId, clientId);

        var response = await PutEditsAsync(
            client, tenantId, created.Id, created.Version,
            RequestFor(
                created,
                paymentMethod: "Efectivo",
                parties: new QuotationPartiesRequest(
                    new QuotationPartyRequest("Sede administrativa", null, null, null, null, null),
                    Shipping: null,
                    BillsToFinalConsumer: true)));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(
            "quotation.billing.final_consumer_conflict", (await ReadProblemAsync(response)).Code);
    }

    /// <summary>Una cotización enviada —todavía editable— con un producto de 100.000 COP sin
    /// impuesto y cantidad 1: el total es 100.000.</summary>
    private static async Task<(QuotationResponse Quotation, Guid ProductId)> CreateEditableQuotationAsync(
        HttpClient client, QepApiFactory factory, Guid tenantId)
    {
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId, baseCop: 100_000m);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        return (quotation, productId);
    }

    private static async Task<QuotationResponse> AddItemAsync(
        HttpClient client, Guid tenantId, Guid quotationId, Guid productId, decimal quantity)
    {
        var response = await client.PostAsJsonAsync(
            $"{QuotationUrl(tenantId, quotationId)}/items",
            new AddQuotationItemRequest(productId, quantity),
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return await ReadQuotationAsync(response);
    }

    // El PUT reemplaza el encabezado entero, así que lo que no se está probando viaja igual a como
    // está guardado — incluida la cuenta de cobro, que si se omite se borra. `Parties` es la
    // excepción: null es el caso normal ("factura/entrega a los datos del cliente"), así que un
    // caller que no lo pasa está pidiendo justo eso, no "no tocar lo guardado".
    private static SaveQuotationRequest RequestFor(
        QuotationResponse quotation,
        DateOnly? validUntil = null,
        string? paymentMethod = null,
        string? notes = null,
        QuotationPartiesRequest? parties = null,
        IReadOnlyList<QuotationEditItemRequest>? items = null) =>
        new(
            validUntil ?? quotation.ValidUntil,
            paymentMethod ?? quotation.PaymentMethod,
            notes,
            parties,
            quotation.BillingAccount is { } billing
                ? new QuotationBillingAccountRequest(
                    billing.CompanyId, billing.BankName, billing.AccountNumber, billing.Currency)
                : null,
            items);

    private static async Task<HttpResponseMessage> PutEditsAsync(
        HttpClient client, Guid tenantId, Guid quotationId, long? version, SaveQuotationRequest body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, QuotationUrl(tenantId, quotationId))
        {
            Content = JsonContent.Create(body),
        };
        if (version is { } value)
        {
            request.Headers.TryAddWithoutValidation("If-Match", $"\"{value}\"");
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static Task<HttpResponseMessage> PreviewEditsAsync(
        HttpClient client, Guid tenantId, Guid quotationId, SaveQuotationRequest body) =>
        client.PostAsJsonAsync(
            $"{QuotationUrl(tenantId, quotationId)}/preview", body, TestContext.Current.CancellationToken);

    private static async Task<QuotationResponse> ReadQuotationAsync(HttpResponseMessage response)
    {
        var quotation = await response.Content.ReadFromJsonAsync<QuotationResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(quotation);
        return quotation;
    }

    private static async Task<QuotationResponse> GetQuotationAsync(
        HttpClient client, Guid tenantId, Guid quotationId)
    {
        var quotation = await client.GetFromJsonAsync<QuotationResponse>(
            QuotationUrl(tenantId, quotationId), TestContext.Current.CancellationToken);
        Assert.NotNull(quotation);
        return quotation;
    }

    // Misma lectura que OrderEditsApiTests.ActionOf: el payload del evento de auditoría lleva la
    // acción en `action`.
    private static string ActionOf(QuotationsOutboxMessage message)
    {
        using var payload = JsonDocument.Parse(message.PayloadJson);
        return payload.RootElement.GetProperty("action").GetString()!;
    }

    private static async Task<ProblemPayload> ReadProblemAsync(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(problem);
        return problem;
    }

    private sealed record ProblemPayload(string Code);
}
