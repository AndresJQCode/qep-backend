using System.Net.Http.Json;
using BuildingBlocks.Application;
using Microsoft.Extensions.DependencyInjection;
using Modules.Quotations.Application;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// Las sondas que Quotations le responde a Storage (spec 2026-09-16), contra Postgres y resueltas del
/// contenedor real, como las consulta Storage: sin su registro, los barridos de Storage no ven los
/// pedidos.
/// </summary>
public sealed class PaymentProofReferenceProbeTests
{
    // D11: el archivo de un comprobante adjunto está referenciado; uno que ningún pedido usa, no.
    [Fact]
    public async Task TheFileReferenceProbeSeesOnlyAttachedProofs()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var quotation = await NewSentQuotationAsync(client, factory, tenantId);
        var attachedFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var looseFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        await ConvertAsync(client, tenantId, quotation.Id, attachedFileId);

        await using var scope = factory.Services.CreateAsyncScope();
        var probe = Assert.Single(
            scope.ServiceProvider.GetServices<IFileReferenceProbe>(),
            candidate => candidate.Source == "quotations");
        Assert.True(await probe.HasReferencesAsync(attachedFileId, TestContext.Current.CancellationToken));
        Assert.False(await probe.HasReferencesAsync(looseFileId, TestContext.Current.CancellationToken));
    }

    private static async Task<QuotationResponse> NewSentQuotationAsync(
        HttpClient client, QepApiFactory factory, Guid tenantId)
    {
        var customerId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        return await CreateSentQuotationAsync(client, factory, tenantId, customerId, productId);
    }

    private static async Task<OrderResponse> ConvertAsync(
        HttpClient client, Guid tenantId, Guid quotationId, Guid proofFileId)
    {
        var response = await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotationId}/order",
            new ConvertQuotationToOrderRequest(
                "FullPaymentReceived", null, [new OrderPaymentProofRequest(proofFileId, 10_000m)]),
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var order = await response.Content.ReadFromJsonAsync<OrderResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(order);
        return order;
    }
}
