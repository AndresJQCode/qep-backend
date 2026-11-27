using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Quotations.Application;
using Modules.Tenancy.Domain;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// El pedido que nace de una cotización de proyecto (spec 2026-11-26 §6.6, §12-C y §12-D): sus líneas y regalos están
/// bloqueados, lo demás se edita como siempre, se opera con el módulo apagado y las listas filtran por <c>kind</c>.
/// </summary>
public sealed class ProjectOrdersApiTests(SharedQuotationsHost sharedHost)
    : OrderApiTests, IClassFixture<SharedQuotationsHost>
{
    private const decimal ProjectTotal = 1_000_000m;

    [Fact]
    public async Task EditingAProjectOrderWithItsLineAndNoGiftsSavesTheRest()
    {
        var setup = await SetUpAsync(await sharedHost.StartTestAsync());
        using var _ = setup.Client;
        var order = await ConvertProjectAsync(setup);

        // La línea tal cual, y de paso lo que el pedido de proyecto ignora: detal, piso y envío.
        var request = new SaveOrderEditsRequest(
            [new OrderEditItemRequest(setup.ServiceId, 1m)], null, "Nota nueva", GlobalScaleFloor: 6, IsRetail: true,
            Shipping: new ShippingRequest(null, "Charged", 15_000m, false));
        var preview = await ReadDetailAsync(await PreviewAsync(setup, order.Id, request));
        var saved = await ReadDetailAsync(await PutAsync(setup, order.Id, order.Version, request));

        foreach (var detail in new[] { preview, saved })
        {
            AssertUntouchedLine(setup, detail);
            Assert.Equal("Nota nueva", detail.Order.Notes);
            Assert.False(detail.Order.IsRetail);
            Assert.Null(detail.Order.GlobalScaleFloor);
            Assert.Null(detail.Order.Shipping.ChargeMode);
            Assert.Equal(ProjectTotal, detail.Order.Total);
        }

        // §12-C: sin líneas o con la lista vacía tampoco cambian, y el precio igual al guardado vale.
        var withoutItems = await ReadDetailAsync(await PutAsync(
            setup, order.Id, saved.Order.Version,
            new SaveOrderEditsRequest(null, null, "Sin líneas en el cuerpo")));
        var emptyLists = await ReadDetailAsync(await PutAsync(
            setup, order.Id, withoutItems.Order.Version,
            new SaveOrderEditsRequest([], null, "Con listas vacías") { Gifts = [] }));
        var samePrice = await ReadDetailAsync(await PutAsync(
            setup, order.Id, emptyLists.Order.Version,
            new SaveOrderEditsRequest(
                [new OrderEditItemRequest(setup.ServiceId, 1m, ProjectTotal)], null, "Con el precio guardado")));
        var emptyPreview = await ReadDetailAsync(
            await PreviewAsync(setup, order.Id, new SaveOrderEditsRequest([], null, "Vista previa")));

        foreach (var detail in new[] { withoutItems, emptyLists, samePrice, emptyPreview })
        {
            AssertUntouchedLine(setup, detail);
            Assert.Equal(ProjectTotal, detail.Order.Total);
        }

        Assert.Equal("Con el precio guardado", samePrice.Order.Notes);
    }

    [Fact]
    public async Task ChangingLinesOrGiftsOfAProjectOrderIsLocked()
    {
        var setup = await SetUpAsync(await sharedHost.StartTestAsync());
        using var _ = setup.Client;
        var other = await CreateProductWithScalesAsync(setup.Client, setup.TenantId);
        var order = await ConvertProjectAsync(setup);
        var line = new OrderEditItemRequest(setup.ServiceId, 1m);
        SaveOrderEditsRequest[] locked =
        [
            new([new OrderEditItemRequest(setup.ServiceId, 2m)], null, "x"),
            new([new OrderEditItemRequest(other, 1m)], null, "x"),
            new([line, new OrderEditItemRequest(other, 1m)], null, "x"),
            new([new OrderEditItemRequest(setup.ServiceId, 1m, ProjectTotal + 1m)], null, "x"),
            new([line], null, "x") { Gifts = [new OrderGiftLineRequest(other, 1m)] },
        ];

        foreach (var request in locked)
        {
            await AssertLockedAsync(await PutAsync(setup, order.Id, order.Version, request));
            await AssertLockedAsync(await PreviewAsync(setup, order.Id, request));
        }

        foreach (var variantId in new[] { other, Guid.CreateVersion7() })
        {
            // Antes del catálogo: una variante que no existe responde lo mismo.
            await AssertLockedAsync(await setup.Client.PostAsJsonAsync(
                OrderItemsUrl(setup.TenantId, order.Id),
                new AddOrderItemsRequest([new OrderItemAdditionRequest(variantId, 1m)]),
                TestContext.Current.CancellationToken));
        }

        var current = await GetAsync(setup, order.Id);
        Assert.Equal((order.Version, ProjectTotal), (current.Order.Version, current.Order.Total));
        AssertUntouchedLine(setup, current);
    }

    // §12-C: «se ignora el envío del cuerpo» incluye no validarlo.
    [Fact]
    public async Task AnInvalidShippingBlockIsIgnoredOnAProjectOrder()
    {
        var setup = await SetUpAsync(await sharedHost.StartTestAsync());
        using var _ = setup.Client;
        var order = await ConvertProjectAsync(setup);
        var request = new SaveOrderEditsRequest(
            [new OrderEditItemRequest(setup.ServiceId, 1m)], null, "Con un envío mal escrito",
            Shipping: new ShippingRequest(null, "Teleport", 15_000.123m, false));

        var preview = await ReadDetailAsync(await PreviewAsync(setup, order.Id, request));
        var saved = await ReadDetailAsync(await PutAsync(setup, order.Id, order.Version, request));

        foreach (var detail in new[] { preview, saved })
        {
            Assert.Equal("Con un envío mal escrito", detail.Order.Notes);
            Assert.Null(detail.Order.Shipping.ChargeMode);
            Assert.Equal(ProjectTotal, detail.Order.Total);
        }
    }

    // P9: un pedido de proyecto existente se opera siempre.
    [Fact]
    public async Task ProjectOrdersKeepWorkingWithTheModuleOff()
    {
        var setup = await SetUpAsync(await sharedHost.StartTestAsync());
        using var _ = setup.Client;
        var order = await ConvertProjectAsync(setup);
        await DisableModuleAsync(setup.Factory, setup.TenantId, TenantModuleKeys.Projects);

        var detail = await GetAsync(setup, order.Id);
        var rows = await ListAsync(setup, string.Empty);
        var filtered = await ListAsync(setup, "?kind=Project");
        var saved = await ReadDetailAsync(await PutAsync(
            setup, order.Id, detail.Order.Version, new SaveOrderEditsRequest(null, null, "Con el módulo apagado")));
        var proofFileId = await CreateAvailablePaymentProofFileAsync(setup.Client, setup.Factory, setup.TenantId);
        var proof = await setup.Client.PostAsJsonAsync(
            OrderProofsUrl(setup.TenantId, order.Id),
            new AddOrderPaymentProofsRequest([new OrderPaymentProofRequest(proofFileId, 100_000m)]),
            TestContext.Current.CancellationToken);
        var pdf = await setup.Client.PostAsync(
            $"{OrderByIdUrl(setup.TenantId, order.Id)}/pdf", content: null, TestContext.Current.CancellationToken);

        Assert.Equal("Project", detail.Order.Kind);
        Assert.Equal(order.Id, Assert.Single(rows.Items).Id);
        Assert.Equal(order.Id, Assert.Single(filtered.Items).Id);
        Assert.Equal("Con el módulo apagado", saved.Order.Notes);
        Assert.True(proof.IsSuccessStatusCode, await proof.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.True(pdf.IsSuccessStatusCode, await pdf.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    // El worker reclama jobs de toda la base: esta prueba levanta su propio host.
    [Fact]
    public async Task TheOrdersListFiltersByKind()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var setup = await SetUpAsync(factory);
        using var _ = setup.Client;
        var project = await ConvertProjectAsync(setup);
        var productId = await CreateProductWithScalesAsync(setup.Client, setup.TenantId);
        var quotation = await CreateSentQuotationAsync(
            setup.Client, factory, setup.TenantId, setup.ClientId, productId);
        var standard = await ReadOrderAsync(await setup.Client.PostAsJsonAsync(
            OrderUrl(setup.TenantId, quotation.Id),
            new ConvertQuotationToOrderRequest(null, []),
            TestContext.Current.CancellationToken));

        foreach (var all in new[] { string.Empty, "?kind=All", "?kind=" })
        {
            var page = await ListAsync(setup, all);
            Assert.Equal(2, page.Total);
            Assert.Equal(
                new HashSet<(Guid, string)> { (project.Id, "Project"), (standard.Id, "Standard") },
                page.Items.Select(row => (row.Id, row.Kind)).ToHashSet());
        }

        var projects = await ListAsync(setup, "?kind=Project");
        var row = Assert.Single(projects.Items);
        Assert.Equal((project.Id, "Project", false, 1), (row.Id, row.Kind, row.RequiresShipping, projects.Total));
        Assert.Equal(standard.Id, Assert.Single((await ListAsync(setup, "?kind=standard")).Items).Id);

        var ordersUrl = $"/api/v1/tenants/{setup.TenantId}/orders";
        foreach (var invalid in new[] { "Other", "1" })
        {
            await AssertKindErrorAsync(await setup.Client.GetAsync(
                $"{ordersUrl}?kind={invalid}", TestContext.Current.CancellationToken));
            await AssertKindErrorAsync(await setup.Client.PostAsync(
                $"{ordersUrl}/export?{ExportRange()}&kind={invalid}", content: null, TestContext.Current.CancellationToken));
        }

        Assert.Equal(
            [standard.OrderNumber], await ExportedNumbersAsync(setup, "&kind=Standard", project, standard));
        Assert.Equal(
            [project.OrderNumber], await ExportedNumbersAsync(setup, "&kind=Project", project, standard));
        Assert.Equal(
            new[] { project.OrderNumber, standard.OrderNumber }.Order(StringComparer.Ordinal),
            await ExportedNumbersAsync(setup, string.Empty, project, standard));
    }

    private sealed record Setup(
        QepApiFactory Factory, HttpClient Client, Guid TenantId, Guid ClientId, Guid ServiceId);

    private static async Task<Setup> SetUpAsync(QepApiFactory factory)
    {
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        await EnableModuleAsync(factory, tenantId, TenantModuleKeys.Projects);
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var serviceId = await ProjectQuotationsApiTests.CreateServiceAsync(client, tenantId);
        return new Setup(factory, client, tenantId, clientId, serviceId);
    }

    // Una cotización de proyecto con cuenta de cobro y precio global, convertida: Draft alcanza para convertir.
    private static async Task<OrderResponse> ConvertProjectAsync(Setup setup)
    {
        var company = await CreateCompanyWithBankAccountAsync(setup.Client, setup.TenantId, "COP");
        var created = await setup.Client.PostAsJsonAsync(
            QuotationsUrl(setup.TenantId),
            new CreateQuotationRequest(
                setup.ClientId, null, null, null, null, null,
                Kind: "Project",
                Project: new ProjectScopeRequest(
                    setup.ServiceId, "Global", ProjectTotal, null,
                    [new ProjectItemRequest(Guid.CreateVersion7(), "Soporte durante el lanzamiento")], "COP"))
            {
                BillingAccount = new QuotationBillingAccountRequest(
                    company.CompanyId, company.BankName, company.AccountNumber, company.Currency),
            },
            TestContext.Current.CancellationToken);
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var quotation = await created.Content.ReadFromJsonAsync<QuotationResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(quotation);

        return await ReadOrderAsync(await setup.Client.PostAsJsonAsync(
            OrderUrl(setup.TenantId, quotation.Id),
            new ConvertQuotationToOrderRequest(null, []),
            TestContext.Current.CancellationToken));
    }

    private static void AssertUntouchedLine(Setup setup, OrderDetailResponse detail)
    {
        var line = Assert.Single(detail.Items);
        Assert.Equal(
            (setup.ServiceId, 1m, ProjectTotal, 0m),
            (line.VariantId, line.Quantity, line.UnitPrice, line.DiscountPercentage));
    }

    private static async Task<HttpResponseMessage> PutAsync(
        Setup setup, Guid orderId, long version, SaveOrderEditsRequest request)
    {
        using var message = new HttpRequestMessage(HttpMethod.Put, OrderByIdUrl(setup.TenantId, orderId))
        {
            Content = JsonContent.Create(request),
        };
        message.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{version}\""));
        return await setup.Client.SendAsync(message, TestContext.Current.CancellationToken);
    }

    private static Task<HttpResponseMessage> PreviewAsync(Setup setup, Guid orderId, SaveOrderEditsRequest request) =>
        setup.Client.PostAsJsonAsync(
            $"{OrderByIdUrl(setup.TenantId, orderId)}/preview", request, TestContext.Current.CancellationToken);

    private static async Task<OrderDetailResponse> ReadDetailAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"Expected OK, got {response.StatusCode}: {text}");
        var detail = await response.Content.ReadFromJsonAsync<OrderDetailResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(detail);
        return detail;
    }

    private static async Task<OrderDetailResponse> GetAsync(Setup setup, Guid orderId) =>
        await ReadDetailAsync(await setup.Client.GetAsync(
            OrderByIdUrl(setup.TenantId, orderId), TestContext.Current.CancellationToken));

    private static async Task<OrdersPageResponse> ListAsync(Setup setup, string query)
    {
        var page = await setup.Client.GetFromJsonAsync<OrdersPageResponse>(
            $"/api/v1/tenants/{setup.TenantId}/orders{query}", TestContext.Current.CancellationToken);
        Assert.NotNull(page);
        return page;
    }

    private static async Task<JsonElement> AssertProblemAsync(HttpResponseMessage response, string code)
    {
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(
            response.StatusCode == HttpStatusCode.UnprocessableEntity, $"Expected 422, got {response.StatusCode}: {text}");
        var body = JsonDocument.Parse(text).RootElement.Clone();
        Assert.Equal(code, body.GetProperty("code").GetString());
        return body;
    }

    private static async Task AssertLockedAsync(HttpResponseMessage response) =>
        await AssertProblemAsync(response, "order.project.items_locked");

    private static async Task AssertKindErrorAsync(HttpResponseMessage response)
    {
        var problem = await AssertProblemAsync(response, "validation.failed");
        Assert.True(problem.GetProperty("errors").TryGetProperty("kind", out _), problem.ToString());
    }

    private static string ExportRange()
    {
        var today = TodayInBogota();
        return string.Create(
            CultureInfo.InvariantCulture, $"convertedFrom={today.AddDays(-1):yyyy-MM-dd}&convertedTo={today:yyyy-MM-dd}");
    }

    // Pide el Excel, corre el job y devuelve cuáles de los dos números de pedido salieron, ordenados.
    private static async Task<string[]> ExportedNumbersAsync(Setup setup, string kind, params OrderResponse[] orders)
    {
        var response = await setup.Client.PostAsync(
            $"/api/v1/tenants/{setup.TenantId}/orders/export?{ExportRange()}{kind}",
            content: null,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = await response.Content.ReadFromJsonAsync<ExportJobAcceptedResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(accepted);
        Assert.Equal(ExportJobRunOutcome.Completed, await RunExportJobAsync(setup.Factory));
        var sheet = ExportWorkbookReader.Read(await setup.Factory.ObjectStorage.DownloadAsync(
            $"exports/tenants/{setup.TenantId:N}/jobs/{accepted.JobId:N}.xlsx", TestContext.Current.CancellationToken));
        var cells = sheet.Rows.Skip(1).SelectMany(cell => cell).ToHashSet(StringComparer.Ordinal);
        return [.. orders.Select(order => order.OrderNumber).Where(cells.Contains).Order(StringComparer.Ordinal)];
    }
}
