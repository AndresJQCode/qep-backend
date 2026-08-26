using System.Net;
using System.Net.Http.Json;
using Modules.Quotations.Application;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>US-13 a US-17: conversión de una cotización enviada en venta.</summary>
public sealed class SaleApiTests
{
    private static string SaleUrl(Guid tenantId, Guid quotationId) =>
        $"{QuotationsUrl(tenantId)}/{quotationId}/sale";

    [Fact]
    public async Task ConvertCreatesTheSaleAndApprovesTheQuotation()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var proofFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);

        var response = await client.PostAsJsonAsync(
            SaleUrl(tenantId, quotation.Id),
            new ConvertQuotationToSaleRequest(
                "FullPaymentReceived", "Pago verificado", [new SalePaymentProofRequest(proofFileId, quotation.Total)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var sale = await response.Content.ReadFromJsonAsync<SaleResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(sale);
        Assert.Equal("Approved", sale.Status);
        Assert.Equal("FullPaymentReceived", sale.PaymentStatus);
        Assert.Equal(quotation.Id, sale.QuotationId);
        Assert.StartsWith(
            $"VEN-{DateTime.UtcNow.Year}-", sale.SaleNumber, StringComparison.Ordinal);
        Assert.Null(sale.RitualCollectionSyncId);
        var proof = Assert.Single(sale.PaymentProofs);
        Assert.Equal(proofFileId, proof.FileId);
        Assert.Equal(quotation.Total, proof.Amount);

        var fetchedQuotation = await client.GetFromJsonAsync<QuotationResponse>(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}", TestContext.Current.CancellationToken);
        Assert.NotNull(fetchedQuotation);
        Assert.Equal("Approved", fetchedQuotation.Status);
    }

    // US-14: sin comprobantes se permite unicamente cuando el pago queda pendiente.
    [Fact]
    public async Task ConvertWithPaymentPendingRequiresNoProofs()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);

        var response = await client.PostAsJsonAsync(
            SaleUrl(tenantId, quotation.Id),
            new ConvertQuotationToSaleRequest("PaymentPending", null, []),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var sale = await response.Content.ReadFromJsonAsync<SaleResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(sale);
        Assert.Empty(sale.PaymentProofs);
    }

    [Fact]
    public async Task ConvertWithoutProofsWhenPaymentIsNotPendingIsUnprocessable()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);

        var response = await client.PostAsJsonAsync(
            SaleUrl(tenantId, quotation.Id),
            new ConvertQuotationToSaleRequest("PartialPaymentReceived", null, []),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    // US-13: "Convertir en venta" solo esta disponible en Sent -- un borrador no se convierte.
    [Fact]
    public async Task ConvertADraftQuotationIsUnprocessable()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var quotation = await CreateQuotationAsync(client, tenantId, clientId);

        var response = await client.PostAsJsonAsync(
            SaleUrl(tenantId, quotation.Id),
            new ConvertQuotationToSaleRequest("PaymentPending", null, []),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task ConvertWithAnInvalidPaymentStatusIsUnprocessable()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);

        var response = await client.PostAsJsonAsync(
            SaleUrl(tenantId, quotation.Id),
            new ConvertQuotationToSaleRequest("NotAStatus", null, []),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task ConvertWithAZeroAmountProofIsUnprocessable()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var proofFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);

        var response = await client.PostAsJsonAsync(
            SaleUrl(tenantId, quotation.Id),
            new ConvertQuotationToSaleRequest(
                "FullPaymentReceived", null, [new SalePaymentProofRequest(proofFileId, 0m)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task GetSaleReturnsTheConvertedSale()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var proofFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var created = await (await client.PostAsJsonAsync(
            SaleUrl(tenantId, quotation.Id),
            new ConvertQuotationToSaleRequest(
                "FullPaymentReceived", null, [new SalePaymentProofRequest(proofFileId, quotation.Total)]),
            TestContext.Current.CancellationToken))
            .Content.ReadFromJsonAsync<SaleResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(created);

        var response = await client.GetAsync(
            SaleUrl(tenantId, quotation.Id), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var fetched = await response.Content.ReadFromJsonAsync<SaleResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(fetched);
        Assert.Equal(created.Id, fetched.Id);
        Assert.Equal(created.SaleNumber, fetched.SaleNumber);
    }

    [Fact]
    public async Task GetSaleForAnUnconvertedQuotationReturnsNotFound()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var quotation = await CreateQuotationAsync(client, tenantId, clientId);

        var response = await client.GetAsync(
            SaleUrl(tenantId, quotation.Id), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ConvertForAnotherTenantIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, owner) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = owner;
        var clientId = await CreateActiveCustomerAsync(owner, tenantId);
        var productId = await CreateProductWithScalesAsync(owner, tenantId);
        var quotation = await CreateSentQuotationAsync(owner, factory, tenantId, clientId, productId);

        var (_, _, otherOwner) = await RegisterTenantAsync(factory, SalesPermissions.SaleManage);
        using var __ = otherOwner;

        var response = await otherOwner.PostAsJsonAsync(
            SaleUrl(tenantId, quotation.Id),
            new ConvertQuotationToSaleRequest("PaymentPending", null, []),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
