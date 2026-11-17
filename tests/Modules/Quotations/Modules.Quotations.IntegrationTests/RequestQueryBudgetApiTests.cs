using System.Net.Http.Json;
using Modules.Quotations.Application;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// Cuántas sentencias SQL cuesta abrir una cotización y un pedido. Postgres corre en otro nodo y cada sentencia en
/// serie es un viaje de red (10-20 ms), así que la métrica es la cantidad de viajes por request, no el tiempo de cada
/// uno. El contador es el <c>ActivityListener</c> de <c>Npgsql</c> filtrado por la traza del request
/// (<see cref="QuotationsApiHarness.StatementsOfAsync"/>), lo mismo que se ve en Tempo.
///
/// El presupuesto es un techo contra regresiones, no una meta: el conteo medido redondeado hacia arriba al múltiplo de
/// 5. Incluye las dos consultas transversales del stub de auth (estado del tenant y módulos). Si una prueba se pone
/// roja, el mensaje trae el listado de sentencias: la nueva que se coló suele ser una lectura por línea.
/// </summary>
/// <remarks>
/// Medido el 2026-11-17, antes de fijar el presupuesto y después de las fases 1 y 2 de consultas por request
/// (porcentajes de impuesto en una lista, memo de retenciones, conexiones en una lectura):
/// <c>GET quotations/{id}</c> = 24 sentencias, <c>GET orders/{id}</c> = 24 sentencias,
/// con dos líneas y cuenta de cobro de una empresa CO.
/// </remarks>
public sealed class RequestQueryBudgetApiTests(SharedQuotationsHost sharedHost) : IClassFixture<SharedQuotationsHost>
{
    private const int QuotationDetailBudget = 25;

    private const int OrderDetailBudget = 25;

    [Fact]
    public async Task GettingAQuotationStaysWithinItsQueryBudget()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var quotation = await CreateQuotationWithTwoLinesAndBillingAsync(client, tenantId);
        var path = $"quotations/{quotation.Id}";

        // La primera pasada calienta lo que se memoriza por host: no es lo que cuesta cada apertura.
        using (var warmUp = Get(tenantId, path))
        {
            await StatementsOfAsync(client, warmUp);
        }

        using var request = Get(tenantId, path);
        var statements = await StatementsOfAsync(client, request);

        Report(statements);
        Assert.True(statements.Count <= QuotationDetailBudget, Describe(statements));
    }

    [Fact]
    public async Task GettingAnOrderStaysWithinItsQueryBudget()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var quotation = await CreateQuotationWithTwoLinesAndBillingAsync(client, tenantId);
        var order = await ConvertAsync(client, tenantId, quotation.Id);
        var path = $"orders/{order.Id}";

        using (var warmUp = Get(tenantId, path))
        {
            await StatementsOfAsync(client, warmUp);
        }

        using var request = Get(tenantId, path);
        var statements = await StatementsOfAsync(client, request);

        Report(statements);
        Assert.True(statements.Count <= OrderDetailBudget, Describe(statements));
    }

    private static HttpRequestMessage Get(Guid tenantId, string path) =>
        new(HttpMethod.Get, $"/api/v1/tenants/{tenantId}/{path}");

    // Cliente activo, dos productos con escalas y la misma tasa de IVA, y la cuenta de cobro de una empresa CO: lo
    // que tiene una cotización real cuando se abre.
    private static async Task<QuotationResponse> CreateQuotationWithTwoLinesAndBillingAsync(HttpClient client, Guid tenantId)
    {
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var taxRateId = await CreateTaxRateAsync(client, tenantId, "IVA 19", 19);
        var first = await CreateProductWithScalesAsync(client, tenantId, taxRateId: taxRateId);
        var second = await CreateProductWithScalesAsync(client, tenantId, taxRateId: taxRateId);
        var billing = await CreateCompanyWithBankAccountAsync(client, tenantId);

        using var response = await client.PostAsJsonAsync(
            QuotationsUrl(tenantId),
            new CreateQuotationRequest(
                clientId,
                TodayInBogota().AddDays(30),
                "Transferencia",
                null,
                null,
                new QuotationBillingAccountRequest(billing.CompanyId, billing.BankName, billing.AccountNumber, billing.Currency),
                Items: [new QuotationEditItemRequest(first, 2m), new QuotationEditItemRequest(second, 3m)]),
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var quotation = await response.Content.ReadFromJsonAsync<QuotationResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(quotation);
        return quotation;
    }

    private static async Task<OrderResponse> ConvertAsync(HttpClient client, Guid tenantId, Guid quotationId)
    {
        using var response = await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotationId}/order",
            new ConvertQuotationToOrderRequest(null, []),
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var order = await response.Content.ReadConvertedOrderAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(order);
        return order;
    }

    private static string Describe(IReadOnlyList<string> statements) =>
        $"{statements.Count} sentencias:{Environment.NewLine}{string.Join(Environment.NewLine, statements)}";

    private static void Report(IReadOnlyList<string> statements) =>
        TestContext.Current.TestOutputHelper?.WriteLine(Describe(statements));
}
