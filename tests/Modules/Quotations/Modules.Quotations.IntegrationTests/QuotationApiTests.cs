using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Quotations.Application;
using Modules.Quotations.Infrastructure.Persistence;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

public sealed class QuotationApiTests
{
    // Con el reloj en el 31 de diciembre a las 23:00 de Bogotá (2027 en UTC): el consecutivo es del
    // año del tenant y la vigencia por defecto cuenta quince días desde su hoy (spec 2026-09-17,
    // puntos 2a y 2c). Con el año de UTC la prueba además fallaba sola cada fin de año.
    [Fact]
    public async Task CreateReturnsADraftWithAGeneratedNumberAndTheResolvedAdvisor()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), utcNow: NewYearsEveInBogota);
        var (tenantId, ownerUserId, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);

        var response = await client.PostAsJsonAsync(
            QuotationsUrl(tenantId),
            new CreateQuotationRequest(clientId, null, "Transferencia bancaria", null, null, null),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var quotation = await response.Content.ReadFromJsonAsync<QuotationResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(quotation);
        Assert.Equal("Draft", quotation.Status);
        Assert.StartsWith("QUO-2026-", quotation.QuotationNumber, StringComparison.Ordinal);
        Assert.Equal(new DateOnly(2027, 1, 15), quotation.ValidUntil);
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

    /// <summary>
    /// La cotización numera con el formato del tenant (spec 2026-09-17 de numeración), y el
    /// consecutivo sin año sale de la fila `year = 0`, que no se reinicia. Reloj fijo en la frontera
    /// de Bogotá: el año no se usa, y que la prueba no dependa del calendario de la máquina.
    /// </summary>
    [Fact]
    public async Task CreateUsesThePrefixAndCounterConfiguredForTheTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), utcNow: NewYearsEveInBogota);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        await DocumentNumberingFormatLookupTests.SetDocumentNumberFormatAsync(
            factory, tenantId, "quotation", "CT", includeYear: false, "", 5);
        await SetQuotationCounterAsync(factory, tenantId, year: 0, nextValue: 90_001L);
        var clientId = await CreateActiveCustomerAsync(client, tenantId);

        var first = await CreateQuotationAsync(client, tenantId, clientId);
        var second = await CreateQuotationAsync(client, tenantId, clientId);

        Assert.Equal("CT90001", first.QuotationNumber);
        Assert.Equal("CT90002", second.QuotationNumber);
    }

    /// <summary>
    /// El domicilio de la cotizacion es el del cliente (su contacto), no la principal de la libreta
    /// (spec 2026-09-18, decision 5): <c>QuotationResponseComposer.cs:96-110</c> entrega
    /// <c>customer.address</c> como respaldo de facturacion y envio, y <c>addresses[0]</c> es la
    /// principal, la que el selector de envio preselecciona. Con la principal en otra ciudad, las
    /// dos cosas se distinguen.
    /// </summary>
    [Fact]
    public async Task CreateShowsTheCustomerAddressAndKeepsThePrincipalFirstInTheAddressBook()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var (home, elsewhere) = await EnsureCityIdsInTwoDepartmentsAsync(client);
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var principalAddressId = await AddPrincipalAddressAsync(client, tenantId, clientId, elsewhere.CityId);

        var quotation = await CreateQuotationAsync(client, tenantId, clientId);

        Assert.NotNull(quotation.Client);
        Assert.Equal("Calle 10 # 45-12", quotation.Client.Address);
        Assert.Equal(home.CityId, quotation.Client.CityId);
        Assert.Equal(home.DepartmentId, quotation.Client.DepartmentId);
        Assert.Equal(2, quotation.Client.Addresses.Count);
        var principal = quotation.Client.Addresses.First();
        Assert.Equal(principalAddressId, principal.Id);
        Assert.True(principal.IsPrincipal);
        Assert.Equal(elsewhere.CityId, principal.CityId);
    }

    /// <summary>El paso 2 del runbook del README para cotizaciones: el mismo UPSERT con GREATEST,
    /// sobre <c>quotation_number_counters</c>.</summary>
    internal static async Task SetQuotationCounterAsync(
        QepApiFactory factory, Guid tenantId, int year, long nextValue)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<QuotationsDbContext>();
        await dbContext.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO quotations.quotation_number_counters (tenant_id, year, next_value)
            VALUES ({tenantId}, {year}, {nextValue})
            ON CONFLICT (tenant_id, year) DO UPDATE
            SET next_value = GREATEST(quotations.quotation_number_counters.next_value, EXCLUDED.next_value)
            """,
            TestContext.Current.CancellationToken);
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
            new CreateQuotationRequest(Guid.NewGuid(), null, null, null, null, null),
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
            new CreateQuotationRequest(clientId, null, null, null, null, null),
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
            new CreateQuotationRequest(clientId, null, null, null, null, null),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CreateWithoutTheManagePermissionIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, readOnly) = await RegisterTenantAsync(
            factory, QuotationsPermissions.QuotationRead);
        using var _ = readOnly;

        var response = await readOnly.PostAsJsonAsync(
            QuotationsUrl(tenantId),
            new CreateQuotationRequest(Guid.NewGuid(), null, null, null, null, null),
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
}
