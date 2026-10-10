using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Quotations.Application;
using Npgsql;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>Spec 2026-10-10 §5.3 (RF10): crear, cambiar el cliente, enviar y convertir en pedido con un cliente
/// incompleto responden 422 <c>quotation.quotation.client_incomplete</c>.</summary>
public sealed class IncompleteCustomerQuotationApiTests
{
    private static async Task MarkIncompleteAsync(string connectionString, Guid customerId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("UPDATE customers.customers SET completeness = 'Incomplete' WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", customerId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
    }

    private static async Task AssertIncompleteAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("quotation.quotation.client_incomplete", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task CreatingAndChangingTheClientToAnIncompleteOneIs422()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var complete = await CreateActiveCustomerAsync(client, tenantId);
        var incomplete = await CreateActiveCustomerAsync(client, tenantId);
        await MarkIncompleteAsync(database.GetConnectionString(), incomplete);
        var draft = await CreateQuotationAsync(client, tenantId, complete);

        var create = await client.PostAsJsonAsync(
            QuotationsUrl(tenantId), new CreateQuotationRequest(incomplete, null, null, null, null, null), TestContext.Current.CancellationToken);
        var change = await client.PutAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{draft.Id}/client", new ChangeQuotationClientRequest(incomplete), TestContext.Current.CancellationToken);

        await AssertIncompleteAsync(create);
        await AssertIncompleteAsync(change);
    }

    [Fact]
    public async Task SendingAndConvertingForAClientThatBecameIncompleteIs422()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var customerId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var sent = await CreateSentQuotationAsync(client, factory, tenantId, customerId, productId);
        // Una segunda, en borrador y lista para enviar: lo mismo que arma CreateSentQuotationAsync antes del /send.
        var billing = await CreateCompanyWithBankAccountAsync(client, tenantId);
        var draft = await CreateQuotationAsync(
            client, tenantId, customerId, billingAccount: new QuotationBillingAccountRequest(billing.CompanyId, billing.BankName, billing.AccountNumber, billing.Currency));
        await client.PostAsJsonAsync($"{QuotationsUrl(tenantId)}/{draft.Id}/items", new AddQuotationItemRequest(productId, 1m), TestContext.Current.CancellationToken);
        var pdfFileId = await CreateAvailablePdfFileAsync(client, factory, tenantId);
        var proofFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        await MarkIncompleteAsync(database.GetConnectionString(), customerId);

        var send = await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{draft.Id}/send", new SendQuotationRequest(pdfFileId), TestContext.Current.CancellationToken);
        var convert = await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{sent.Id}/order",
            new ConvertQuotationToOrderRequest("FullPaymentReceived", null, [new OrderPaymentProofRequest(proofFileId, sent.Total)]),
            TestContext.Current.CancellationToken);

        await AssertIncompleteAsync(send);
        await AssertIncompleteAsync(convert);
    }
}
