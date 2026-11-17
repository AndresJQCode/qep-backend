using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Quotations.Infrastructure.Persistence;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// Marcar como enviada: el asesor ya le hizo llegar la cotización al cliente por fuera de QEP y
/// sólo quiere que el estado lo diga. No genera PDF ni manda nada, y deja <c>MarkedSent</c> en el
/// historial en vez de <c>Sent</c>. El evento de outbox no se afirma acá: en el host compartido
/// un conteo sin tenant depende de las demás pruebas de la clase, y lo cubre la unitaria.
/// </summary>
public sealed class QuotationMarkSentApiTests(SharedQuotationsHost sharedHost) : IClassFixture<SharedQuotationsHost>
{
    [Fact]
    public async Task MarkSentMovesADraftToSent()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var quotation = await CreateSendableQuotationAsync(client, tenantId, clientId);

        var response = await MarkSentAsync(client, tenantId, quotation.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var marked = await response.Content.ReadFromJsonAsync<QuotationResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(marked);
        Assert.Equal("Sent", marked.Status);
        Assert.NotNull(marked.SentAt);
        var history = await HistoryAsync(client, tenantId, quotation.Id);
        Assert.Contains(history.Items, entry => entry.EventType == "MarkedSent");
    }

    // Marcar una que QEP ya envió no es un error: el asesor la volvió a mandar por fuera, y el
    // historial lo registra como otra línea al lado del envío original.
    [Fact]
    public async Task MarkingAnAlreadySentQuotationAddsAnotherEntry()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);

        var response = await MarkSentAsync(client, tenantId, quotation.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var history = await HistoryAsync(client, tenantId, quotation.Id);
        Assert.Contains(history.Items, entry => entry.EventType == "Sent");
        Assert.Contains(history.Items, entry => entry.EventType == "MarkedSent");
    }

    // Por la API no se puede crear una cotización sin vigencia (`CreateQuotation` le pone una por
    // defecto), así que se le quita directo en la base, filtrando por su id. Sin vigencia nunca
    // vencería, y marcarla enviada la dejaría convertible para siempre.
    [Fact]
    public async Task MarkSentWithoutValidUntilIsUnprocessable()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var quotation = await CreateSendableQuotationAsync(client, tenantId, clientId);
        await ClearValidUntilAsync(factory, new QuotationId(quotation.Id));

        var response = await MarkSentAsync(client, tenantId, quotation.Id);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains(
            "quotation.quotation.valid_until_required",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
        var fetched = await client.GetFromJsonAsync<QuotationResponse>(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}", TestContext.Current.CancellationToken);
        Assert.NotNull(fetched);
        Assert.Equal("Draft", fetched.Status);
    }

    [Fact]
    public async Task MarkSentAVoidedQuotationIsUnprocessable()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var quotation = await CreateSendableQuotationAsync(client, tenantId, clientId);
        var voided = await client.PostAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/void",
            content: null,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, voided.StatusCode);

        var response = await MarkSentAsync(client, tenantId, quotation.Id);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains(
            "quotation.quotation.not_draft",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task MarkSentForAnotherTenantIsForbidden()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, owner) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = owner;
        var clientId = await CreateActiveCustomerAsync(owner, tenantId);
        var quotation = await CreateSendableQuotationAsync(owner, tenantId, clientId);

        var (_, _, otherOwner) = await RegisterTenantAsync(
            factory, QuotationsPermissions.QuotationManage);
        using var __ = otherOwner;

        var response = await MarkSentAsync(otherOwner, tenantId, quotation.Id);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static Task<HttpResponseMessage> MarkSentAsync(HttpClient client, Guid tenantId, Guid quotationId) =>
        client.PostAsync(
            $"{QuotationsUrl(tenantId)}/{quotationId}/mark-sent",
            content: null,
            TestContext.Current.CancellationToken);

    private static async Task<QuotationHistoryResponse> HistoryAsync(
        HttpClient client, Guid tenantId, Guid quotationId)
    {
        var history = await client.GetFromJsonAsync<QuotationHistoryResponse>(
            $"{QuotationsUrl(tenantId)}/{quotationId}/history", TestContext.Current.CancellationToken);
        Assert.NotNull(history);
        return history;
    }

    private static async Task ClearValidUntilAsync(QepApiFactory factory, QuotationId quotationId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<QuotationsDbContext>();
        var updated = await dbContext.Quotations
            .Where(quotation => quotation.Id == quotationId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(quotation => quotation.ValidUntil, (DateOnly?)null),
                TestContext.Current.CancellationToken);
        Assert.Equal(1, updated);
    }
}
