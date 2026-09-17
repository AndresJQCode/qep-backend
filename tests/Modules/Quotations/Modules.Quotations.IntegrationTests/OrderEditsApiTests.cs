using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Quotations.Infrastructure.Persistence;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// Spec 2026-09-17: editar un pedido como borrador. El guardado atómico
/// (<c>PUT /quotations/{id}/order</c> con <c>If-Match</c>) y el cálculo previo
/// (<c>POST /quotations/{id}/order/preview</c>), contra Postgres real.
/// </summary>
public sealed class OrderEditsApiTests
{
    private static string OrderUrl(Guid tenantId, Guid quotationId) =>
        $"{QuotationsUrl(tenantId)}/{quotationId}/order";

    // Decisión 4: el cálculo previo muta el agregado en memoria, así que la lectura no puede dejar
    // nada en el change tracker que un SaveChangesAsync del mismo scope llegue a persistir.
    [Fact]
    public async Task FindUntrackedLoadsBothAggregatesWithoutTrackingThem()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var proofFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        (await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest(
                "FullPaymentReceived", null, [new OrderPaymentProofRequest(proofFileId, quotation.Total)]),
            TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        await using var scope = factory.Services.CreateAsyncScope();
        var quotations = scope.ServiceProvider.GetRequiredService<IQuotationRepository>();
        var orders = scope.ServiceProvider.GetRequiredService<IOrderRepository>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IQuotationsUnitOfWork>();
        var dbContext = scope.ServiceProvider.GetRequiredService<QuotationsDbContext>();
        var quotationId = new QuotationId(quotation.Id);

        var loadedQuotation = await quotations.FindUntrackedAsync(
            tenantId, quotationId, TestContext.Current.CancellationToken);
        var loadedOrder = await orders.FindUntrackedAsync(
            tenantId, quotationId, TestContext.Current.CancellationToken);

        Assert.NotNull(loadedQuotation);
        Assert.NotNull(loadedOrder);
        Assert.Single(loadedQuotation.Items);
        Assert.Single(loadedOrder.PaymentProofs);
        Assert.Empty(dbContext.ChangeTracker.Entries());

        loadedOrder.UpdateNotes("Borrador que no se guarda", DateTimeOffset.UtcNow);
        Assert.Equal(0, await unitOfWork.SaveChangesAsync(TestContext.Current.CancellationToken));

        // El filtro de tenant es parte de la consulta, como en FindAsync.
        Assert.Null(await quotations.FindUntrackedAsync(
            Guid.CreateVersion7(), quotationId, TestContext.Current.CancellationToken));
        Assert.Null(await orders.FindUntrackedAsync(
            Guid.CreateVersion7(), quotationId, TestContext.Current.CancellationToken));
    }

    private sealed record ProblemPayload(string Code);
}
