using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using Modules.Quotations.Application;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// Las sentencias de un cálculo previo no crecen con las líneas. Esta ruta corre mientras la persona
/// escribe y en producción llegó a 130 sentencias por request, una lectura del catálogo por línea
/// (<c>QuotationItemEdits</c>). Con doble del catálogo sólo se ve cuántas veces se llama al puerto; lo
/// que cuesta es el SQL de verdad, y eso sólo se cuenta contra Postgres.
///
/// Todas las líneas comparten tasa de impuesto: el adaptador hace una lectura por tasa distinta, a
/// propósito, y eso no depende de cuántas líneas haya.
/// </summary>
public sealed class QuotationPreviewStatementCountApiTests
{
    private const int FewLines = 2;

    private const int ManyLines = 10;

    [Fact]
    public async Task ThePreviewOfANewQuotationDoesNotReadMoreForMoreLines()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var products = await CreateProductsAsync(client, tenantId);
        var url = $"{QuotationsUrl(tenantId)}/preview";

        CreateQuotationRequest BodyWith(int lines) => new(
            clientId, null, null, null, null, null,
            Items: products.Take(lines).Select(product => new QuotationEditItemRequest(product, 2m)).ToArray());

        // La primera pasada no cuenta: lo que se cachea por host no debe parecer una diferencia de líneas.
        await StatementsOfAsync(client, url, BodyWith(1));
        var few = await StatementsOfAsync(client, url, BodyWith(FewLines));
        var many = await StatementsOfAsync(client, url, BodyWith(ManyLines));

        Report(few, many);
        Assert.Equal(few.Count, many.Count);
    }

    [Fact]
    public async Task ThePreviewOfASavedQuotationDoesNotReadMoreForMoreChangedLines()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var products = await CreateProductsAsync(client, tenantId);

        async Task<(string Url, SaveQuotationRequest Body)> SavedWithAsync(int lines)
        {
            using var response = await client.PostAsJsonAsync(
                QuotationsUrl(tenantId),
                new CreateQuotationRequest(
                    clientId, null, null, null, null, null,
                    Items: products.Take(lines).Select(product => new QuotationEditItemRequest(product, 2m)).ToArray()),
                TestContext.Current.CancellationToken);
            response.EnsureSuccessStatusCode();
            var saved = await response.Content.ReadFromJsonAsync<QuotationResponse>(TestContext.Current.CancellationToken);
            Assert.NotNull(saved);

            return (
                $"{QuotationsUrl(tenantId)}/{saved.Id}/preview",
                new SaveQuotationRequest(
                    saved.ValidUntil, saved.PaymentMethod, saved.Notes, Parties: null, BillingAccount: null,
                    saved.Items.Select(item => new QuotationEditItemRequest(item.ProductId, item.Quantity + 1m)).ToArray(),
                    GlobalScaleFloor: null));
        }

        var warmUp = await SavedWithAsync(1);
        var few = await SavedWithAsync(FewLines);
        var many = await SavedWithAsync(ManyLines);

        await StatementsOfAsync(client, warmUp.Url, warmUp.Body);
        var fewStatements = await StatementsOfAsync(client, few.Url, few.Body);
        var manyStatements = await StatementsOfAsync(client, many.Url, many.Body);

        Report(fewStatements, manyStatements);
        Assert.Equal(fewStatements.Count, manyStatements.Count);
    }

    // El pedido pendiente recorre OrderItemEdits, el gemelo de QuotationItemEdits, en el mismo tecleo.
    [Fact]
    public async Task ThePreviewOfASavedOrderDoesNotReadMoreForMoreChangedLines()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var products = await CreateProductsAsync(client, tenantId);
        var billing = await CreateCompanyWithBankAccountAsync(client, tenantId);
        var ordersUrl = $"/api/v1/tenants/{tenantId}/orders";

        async Task<(string Url, SaveOrderEditsRequest Body)> SavedWithAsync(int lines)
        {
            using var response = await client.PostAsJsonAsync(
                ordersUrl,
                new CreateOrderRequest(
                    clientId, "Transferencia", null, false, null, null,
                    new QuotationBillingAccountRequest(billing.CompanyId, billing.BankName, billing.AccountNumber, billing.Currency),
                    products.Take(lines).Select(product => new OrderEditItemRequest(product, 2m)).ToArray(),
                    []),
                TestContext.Current.CancellationToken);
            response.EnsureSuccessStatusCode();
            var saved = await response.Content.ReadFromJsonAsync<OrderDetailResponse>(TestContext.Current.CancellationToken);
            Assert.NotNull(saved);

            return (
                $"{ordersUrl}/{saved.Order.Id}/preview",
                new SaveOrderEditsRequest(
                    saved.Items.Select(item => new OrderEditItemRequest(item.ProductId, item.Quantity + 1m)).ToArray(),
                    new OrderEditProofsRequest(null, null, null),
                    Notes: null));
        }

        var warmUp = await SavedWithAsync(1);
        var few = await SavedWithAsync(FewLines);
        var many = await SavedWithAsync(ManyLines);

        await StatementsOfAsync(client, warmUp.Url, warmUp.Body);
        var fewStatements = await StatementsOfAsync(client, few.Url, few.Body);
        var manyStatements = await StatementsOfAsync(client, many.Url, many.Body);

        Report(fewStatements, manyStatements);
        Assert.Equal(fewStatements.Count, manyStatements.Count);
    }

    // OrderDraftBuilder precia el pedido nuevo: el mismo camino de «Nuevo pedido» y de guardarlo.
    [Fact]
    public async Task ThePreviewOfANewOrderDoesNotReadMoreForMoreLines()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var products = await CreateProductsAsync(client, tenantId);
        var billing = await BillingAccountOfNewCompanyAsync(client, tenantId);
        var url = $"{OrdersUrl(tenantId)}/preview";

        CreateOrderRequest BodyWith(int lines) => OrderWith(clientId, billing, products.Take(lines));

        await StatementsOfAsync(client, url, BodyWith(1));
        var few = await StatementsOfAsync(client, url, BodyWith(FewLines));
        var many = await StatementsOfAsync(client, url, BodyWith(ManyLines));

        Report(few, many);
        Assert.Equal(few.Count, many.Count);
    }

    // Agregar sí escribe más con más líneas, y eso es legítimo: lo que no debe crecer es lo que lee.
    [Fact]
    public async Task AddingItemsToAnOrderDoesNotReadMoreForMoreLines()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var products = await CreateProductsAsync(client, tenantId);
        var billing = await BillingAccountOfNewCompanyAsync(client, tenantId);

        async Task<string> ItemsUrlOfNewOrderAsync()
        {
            using var response = await client.PostAsJsonAsync(
                OrdersUrl(tenantId),
                OrderWith(clientId, billing, products.Take(1)),
                TestContext.Current.CancellationToken);
            response.EnsureSuccessStatusCode();
            var saved = await response.Content.ReadFromJsonAsync<OrderDetailResponse>(TestContext.Current.CancellationToken);
            Assert.NotNull(saved);

            return $"{OrdersUrl(tenantId)}/{saved.Order.Id}/items";
        }

        AddOrderItemsRequest BodyWith(int lines) => new(
            products.Skip(1).Take(lines).Select(product => new OrderItemAdditionRequest(product, 2m)).ToArray());

        var warmUp = await ItemsUrlOfNewOrderAsync();
        var fewUrl = await ItemsUrlOfNewOrderAsync();
        var manyUrl = await ItemsUrlOfNewOrderAsync();

        await StatementsOfAsync(client, warmUp, BodyWith(1));
        var few = ReadsOf(await StatementsOfAsync(client, fewUrl, BodyWith(FewLines)));
        var many = ReadsOf(await StatementsOfAsync(client, manyUrl, BodyWith(ManyLines)));

        Report(few, many);
        Assert.Equal(few.Length, many.Length);
    }

    [Fact]
    public async Task TheQuotationBatchDoesNotReadMoreForMoreLines()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var products = await CreateProductsAsync(client, tenantId);

        async Task<string> BatchUrlOfNewQuotationAsync()
        {
            using var response = await client.PostAsJsonAsync(
                QuotationsUrl(tenantId),
                new CreateQuotationRequest(
                    clientId, null, null, null, null, null,
                    Items: [new QuotationEditItemRequest(products[0], 2m)]),
                TestContext.Current.CancellationToken);
            response.EnsureSuccessStatusCode();
            var saved = await response.Content.ReadFromJsonAsync<QuotationResponse>(TestContext.Current.CancellationToken);
            Assert.NotNull(saved);

            return $"{QuotationsUrl(tenantId)}/{saved.Id}/items/batch";
        }

        BatchUpdateQuotationItemsRequest BodyWith(int lines) => new(
            products.Skip(1).Take(lines).Select(product => new BatchQuotationItemAdditionRequest(product, 2m)).ToArray(),
            []);

        var warmUp = await BatchUrlOfNewQuotationAsync();
        var fewUrl = await BatchUrlOfNewQuotationAsync();
        var manyUrl = await BatchUrlOfNewQuotationAsync();

        await StatementsOfAsync(client, warmUp, BodyWith(1));
        var few = ReadsOf(await StatementsOfAsync(client, fewUrl, BodyWith(FewLines)));
        var many = ReadsOf(await StatementsOfAsync(client, manyUrl, BodyWith(ManyLines)));

        Report(few, many);
        Assert.Equal(few.Length, many.Length);
    }

    private static string OrdersUrl(Guid tenantId) => $"/api/v1/tenants/{tenantId}/orders";

    private static async Task<QuotationBillingAccountRequest> BillingAccountOfNewCompanyAsync(HttpClient client, Guid tenantId)
    {
        var billing = await CreateCompanyWithBankAccountAsync(client, tenantId);
        return new QuotationBillingAccountRequest(billing.CompanyId, billing.BankName, billing.AccountNumber, billing.Currency);
    }

    private static CreateOrderRequest OrderWith(
        Guid clientId, QuotationBillingAccountRequest billing, IEnumerable<Guid> products) => new(
            clientId, "Transferencia", null, false, null, null, billing,
            products.Select(product => new OrderEditItemRequest(product, 2m)).ToArray(),
            []);

    // Una línea base más que ManyLines: las rutas que agregan parten de un documento con una línea.
    private static async Task<Guid[]> CreateProductsAsync(HttpClient client, Guid tenantId)
    {
        var taxRateId = await CreateTaxRateAsync(client, tenantId, "IVA 19", 19);
        var products = new Guid[ManyLines + 1];
        for (var index = 0; index < products.Length; index++)
        {
            products[index] = await CreateProductWithScalesAsync(client, tenantId, taxRateId: taxRateId);
        }

        return products;
    }

    /// <summary>
    /// Las sentencias de un solo request. El <c>ActivityListener</c> es global al proceso y las clases de
    /// prueba corren en paralelo, y en este mismo host hay workers leyendo la base: lo único que separa el
    /// request de todo eso es su traza, que entra por el header <c>traceparent</c> y que comparten todos sus
    /// spans. Filtrar por base o por host contaría también a los workers.
    /// </summary>
    private static async Task<IReadOnlyList<string>> StatementsOfAsync<TBody>(HttpClient client, string url, TBody body)
    {
        var traceId = ActivityTraceId.CreateRandom();
        var statements = new ConcurrentQueue<string>();

        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is "Npgsql" or "Microsoft.AspNetCore",
            Sample = (ref ActivityCreationOptions<ActivityContext> options) =>
                options.TraceId == traceId
                    ? ActivitySamplingResult.AllDataAndRecorded
                    : ActivitySamplingResult.None,
            ActivityStopped = activity =>
            {
                if (activity.Source.Name == "Npgsql" && activity.TraceId == traceId)
                {
                    statements.Enqueue(activity.GetTagItem("db.query.text")?.ToString() ?? activity.DisplayName);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);

        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        request.Headers.Add("traceparent", $"00-{traceId}-{ActivitySpanId.CreateRandom()}-01");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        // Sin esto, una traza que dejara de propagarse daría 0 = 0 y la prueba pasaría sin medir nada.
        Assert.NotEmpty(statements);

        return statements.ToArray();
    }

    /// <summary>
    /// Sólo las lecturas. El filtro va por el texto, así que depende de que <c>db.query.text</c> traiga el
    /// SQL: si dejara de traerlo no quedaría ninguna y la prueba compararía 0 con 0, por eso lo exige.
    /// </summary>
    private static string[] ReadsOf(IReadOnlyList<string> statements)
    {
        var reads = statements
            .Where(statement => statement.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.NotEmpty(reads);

        return reads;
    }

    private static void Report(IReadOnlyList<string> few, IReadOnlyList<string> many)
    {
        var output = TestContext.Current.TestOutputHelper;
        output?.WriteLine($"Sentencias: {FewLines} líneas = {few.Count}; {ManyLines} líneas = {many.Count}");
        foreach (var statement in many)
        {
            output?.WriteLine(statement.ReplaceLineEndings(" "));
        }
    }
}
