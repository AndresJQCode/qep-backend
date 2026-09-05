using System.Net;
using System.Net.Http.Json;
using Modules.Quotations.Application;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

public sealed class QuotationApiTests
{
    [Fact]
    public async Task CreateReturnsADraftWithAGeneratedNumberAndTheResolvedAdvisor()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, ownerUserId, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);

        var response = await client.PostAsJsonAsync(
            QuotationsUrl(tenantId),
            new CreateQuotationRequest(clientId, null, "Transferencia bancaria", null, null),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var quotation = await response.Content.ReadFromJsonAsync<QuotationResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(quotation);
        Assert.Equal("Draft", quotation.Status);
        Assert.StartsWith(
            $"QUO-{DateTime.UtcNow.Year}-", quotation.QuotationNumber, StringComparison.Ordinal);
        Assert.Equal(clientId, quotation.ClientId);
        // CreatedBy/AdvisorId son el MembershipId que IMembershipDirectory resolvio para el
        // dueño registrado -- no el subject id crudo del header (distinto por diseño, §1.4).
        Assert.NotEqual(Guid.Empty, quotation.CreatedBy);
        Assert.NotEqual(ownerUserId, quotation.CreatedBy);
        Assert.Equal(quotation.CreatedBy, quotation.AdvisorId);
        Assert.Empty(quotation.Items);
        Assert.Equal(0m, quotation.Total);
        // RN-013: el impuesto es la suma del de cada línea -- sin líneas, no hay impuesto.
        Assert.Equal(0m, quotation.TaxPercentage);
    }

    // Snapshot al crear (Quotation.CustomerVatSurplus): un cliente con excedente de IVA no paga
    // IVA en la cotizacion, sea cual sea la tasa de cada linea.
    [Fact]
    public async Task AddingAnItemForAVatSurplusCustomerLeavesTheHeaderTaxAtZero()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId, vatSurplus: true);
        var taxRateId = await CreateTaxRateAsync(client, tenantId, "IVA 19%", 19);
        // 119_000 con el IVA del 19% ya adentro: la base es 100_000 redondo, que es lo que
        // dejan las aserciones de abajo legibles.
        var productId = await CreateProductWithScalesAsync(
            client, tenantId, baseCop: 119_000m, taxRateId: taxRateId);
        var quotation = await CreateQuotationAsync(client, tenantId, clientId);

        var response = await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/items",
            new AddQuotationItemRequest(productId, 1m),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<QuotationResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(updated);
        Assert.Equal(100_000m, updated.Subtotal);
        Assert.Equal(0m, updated.TaxAmount);
        Assert.Equal(0m, updated.TaxPercentage);
        Assert.Equal(100_000m, updated.Total);
        Assert.Equal(0m, updated.RetentionAmount);
        Assert.Equal(100_000m, updated.NetTotal);
    }

    // Snapshot al crear (Quotation.CustomerWithRetention): 2.5% de lo facturado sin IVA, y resta
    // del neto a cobrar (no del Total facturado).
    [Fact]
    public async Task AddingAnItemForAWithRetentionCustomerComputesRetentionAndNetTotal()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId, withRetention: true);
        var taxRateId = await CreateTaxRateAsync(client, tenantId, "IVA 19%", 19);
        // 119_000 con el IVA del 19% ya adentro: la base es 100_000 redondo, que es lo que
        // dejan las aserciones de abajo legibles.
        var productId = await CreateProductWithScalesAsync(
            client, tenantId, baseCop: 119_000m, taxRateId: taxRateId);
        var quotation = await CreateQuotationAsync(client, tenantId, clientId);

        var response = await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/items",
            new AddQuotationItemRequest(productId, 1m),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<QuotationResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(updated);
        // subtotal = 100_000; tax = 19_000; total = 119_000; retencion = 100_000 * 0.025 = 2_500
        Assert.Equal(100_000m, updated.Subtotal);
        Assert.Equal(119_000m, updated.Total);
        Assert.Equal(2_500m, updated.RetentionAmount);
        Assert.Equal(116_500m, updated.NetTotal);
    }

    [Fact]
    public async Task CreateForAnUnknownClientIsUnprocessable()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;

        var response = await client.PostAsJsonAsync(
            QuotationsUrl(tenantId),
            new CreateQuotationRequest(Guid.NewGuid(), null, null, null, null),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    // US-1/US-18: no se cotiza a un cliente inactivo.
    [Fact]
    public async Task CreateForAnInactiveClientIsUnprocessable()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        await DeactivateCustomerAsync(client, tenantId, clientId);

        var response = await client.PostAsJsonAsync(
            QuotationsUrl(tenantId),
            new CreateQuotationRequest(clientId, null, null, null, null),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    // El handler revalida el tenant activo del llamador contra el tenant de la ruta, asi que
    // esto es 403 y no 404 -- un 404 confirmaria que el cliente existe en otro tenant.
    [Fact]
    public async Task CreateForAnotherTenantIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, owner) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = owner;
        var clientId = await CreateActiveCustomerAsync(owner, tenantId);

        var (_, _, otherOwner) = await RegisterTenantAsync(factory, QuotationsPermissions.QuotationManage);
        using var __ = otherOwner;

        var response = await otherOwner.PostAsJsonAsync(
            QuotationsUrl(tenantId),
            new CreateQuotationRequest(clientId, null, null, null, null),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // TEMPORAL (a pedido, 2026-08-24): la restriccion por permiso esta desactivada en
    // QuotationEndpoints/QuotationsAuthorization mientras se prueba el flujo manualmente. Esta
    // prueba queda documentada pero saltada -- reactivarla junto con las políticas comentadas.
    [Fact(Skip = "Restriccion por permiso desactivada temporalmente (ver QuotationsAuthorization).")]
    public async Task CreateWithoutTheManagePermissionIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, readOnly) = await RegisterTenantAsync(
            factory, QuotationsPermissions.QuotationRead);
        using var _ = readOnly;

        var response = await readOnly.PostAsJsonAsync(
            QuotationsUrl(tenantId),
            new CreateQuotationRequest(Guid.NewGuid(), null, null, null, null),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetReturnsTheCreatedQuotation()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var created = await CreateQuotationAsync(client, tenantId, clientId);

        var response = await client.GetAsync(
            $"{QuotationsUrl(tenantId)}/{created.Id}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var fetched = await response.Content.ReadFromJsonAsync<QuotationResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(fetched);
        Assert.Equal(created.Id, fetched.Id);
        Assert.Equal(created.QuotationNumber, fetched.QuotationNumber);
    }

    // Regresión: retención/excedente de IVA son hechos del cliente, no una foto congelada al
    // crear -- si el cliente cambia mientras la cotización sigue editable (Draft/Sent), la
    // próxima lectura tiene que notarlo (Quotation.RefreshCustomerTaxProfile), no seguir
    // mostrando el IVA/retención de cuando se creó.
    [Fact]
    public async Task GetRefreshesRetentionAndVatSurplusFromTheCurrentCustomer()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var identificationNumber = "900.111.222-3";
        var clientId = await CreateActiveCustomerAsync(
            client, tenantId, identificationNumber, withRetention: false, vatSurplus: false);
        var taxRateId = await CreateTaxRateAsync(client, tenantId, "IVA 19%", 19);
        // 119_000 con el IVA del 19% ya adentro: la base es 100_000 redondo, que es lo que
        // dejan las aserciones de abajo legibles.
        var productId = await CreateProductWithScalesAsync(
            client, tenantId, baseCop: 119_000m, taxRateId: taxRateId);
        var created = await CreateQuotationAsync(client, tenantId, clientId);
        await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{created.Id}/items",
            new AddQuotationItemRequest(productId, 1m),
            TestContext.Current.CancellationToken);

        // El cliente activa retención y excedente de IVA después de creada la cotización.
        await UpdateCustomerRetentionAsync(
            client, tenantId, clientId, identificationNumber, withRetention: true, vatSurplus: true);

        var response = await client.GetAsync(
            $"{QuotationsUrl(tenantId)}/{created.Id}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var fetched = await response.Content.ReadFromJsonAsync<QuotationResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(fetched);
        // subtotal = 100_000; excedente de IVA -> impuesto 0; retencion = 100_000 * 0.025 = 2_500
        Assert.Equal(100_000m, fetched.Subtotal);
        Assert.True(fetched.CustomerVatSurplus);
        Assert.Equal(0m, fetched.TaxAmount);
        Assert.Equal(100_000m, fetched.Total);
        Assert.Equal(2_500m, fetched.RetentionAmount);
        Assert.Equal(97_500m, fetched.NetTotal);
    }

    // El mismo cambio de cliente, pero después de anular la cotización -- una vez Voided queda
    // de sólo lectura para siempre (Quotation.RefreshCustomerTaxProfile no toca nada ahí), así
    // que el resumen histórico no se mueve aunque el cliente cambie después.
    [Fact]
    public async Task GetDoesNotRefreshRetentionOrVatSurplusForAVoidedQuotation()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var identificationNumber = "900.111.222-4";
        var clientId = await CreateActiveCustomerAsync(
            client, tenantId, identificationNumber, withRetention: false, vatSurplus: false);
        var created = await CreateQuotationAsync(client, tenantId, clientId);
        await client.PostAsync(
            $"{QuotationsUrl(tenantId)}/{created.Id}/void",
            content: null,
            TestContext.Current.CancellationToken);

        await UpdateCustomerRetentionAsync(
            client, tenantId, clientId, identificationNumber, withRetention: true, vatSurplus: true);

        var response = await client.GetAsync(
            $"{QuotationsUrl(tenantId)}/{created.Id}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var fetched = await response.Content.ReadFromJsonAsync<QuotationResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(fetched);
        Assert.Equal("Voided", fetched.Status);
        Assert.False(fetched.CustomerVatSurplus);
        Assert.Equal(0m, fetched.RetentionAmount);
    }

    [Fact]
    public async Task GetUnknownQuotationReturnsNotFound()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;

        var response = await client.GetAsync(
            $"{QuotationsUrl(tenantId)}/{Guid.NewGuid()}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // US-6/US-10: PATCH reemplaza el encabezado entero, incluidas las sobrescrituras de
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

        var validUntil = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30));
        var response = await client.PatchAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{created.Id}",
            new UpdateQuotationRequest(
                validUntil,
                "Efectivo",
                "Nota de prueba",
                new QuotationPartiesRequest(
                    new QuotationPartyRequest(
                        "Nombre alterno", null, null, null, null, null),
                    Shipping: null)),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<QuotationResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(updated);
        Assert.Equal(validUntil, updated.ValidUntil);
        Assert.Equal("Efectivo", updated.PaymentMethod);
        Assert.Equal("Nota de prueba", updated.Notes);
        var billing = Assert.Single(updated.Parties);
        Assert.Equal("Billing", billing.Role);
        Assert.Equal("Nombre alterno", billing.Name);
        Assert.NotEqual(created.UpdatedAt, updated.UpdatedAt);
    }
}
