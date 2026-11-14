using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Quotations.Application;
using Modules.Quotations.Infrastructure.Persistence;
using Npgsql;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>Comprobantes de pago de un pedido: agregar, corregir, reemplazar y quitar.</summary>
public sealed class OrderPaymentProofsApiTests(SharedQuotationsHost sharedHost) : OrderApiTests, IClassFixture<SharedQuotationsHost>
{
    // A pedido (2026-09): "Aprobar pedido" se bloquea mientras el pago no esta completo, y esta
    // ruta es la forma de destrabarlo sin recrear el pedido -- cargar lo que falto al convertir.
    [Fact]
    public async Task AddPaymentProofsAddsThemAndUpdatesThePaymentStatus()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var created = await ReadOrderAsync(await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest(null, []),
            TestContext.Current.CancellationToken));

        var proofFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var response = await client.PostAsJsonAsync(
            OrderProofsUrl(tenantId, created.Id),
            new AddOrderPaymentProofsRequest(
                [new OrderPaymentProofRequest(proofFileId, quotation.Total)],
                Notes: "Pago completado por transferencia"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var order = await response.Content.ReadFromJsonAsync<OrderResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(order);
        Assert.Equal("PendingVerification", order.PaymentStatus);
        Assert.Equal("Pago completado por transferencia", order.Notes);
        var proof = Assert.Single(order.PaymentProofs);
        Assert.Equal(proofFileId, proof.FileId);
        Assert.Equal(quotation.Total, proof.Amount);
    }

    // A pedido (2026-09): corregir el monto de un comprobante ya cargado, en el mismo request
    // que sumaria uno nuevo -- un solo viaje de red para las dos cosas.
    [Fact]
    public async Task AddPaymentProofsCorrectsAnExistingProofAmount()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var firstProofFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var convert = await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest(
                null,
                [new OrderPaymentProofRequest(firstProofFileId, 10_000m)]),
            TestContext.Current.CancellationToken);
        convert.EnsureSuccessStatusCode();
        var created = await convert.Content.ReadConvertedOrderAsync(
            TestContext.Current.CancellationToken);
        Assert.NotNull(created);
        var existingProofId = Assert.Single(created.PaymentProofs).Id;

        var response = await client.PostAsJsonAsync(
            OrderProofsUrl(tenantId, created.Id),
            new AddOrderPaymentProofsRequest(
                [],
                UpdatedProofs: [new OrderPaymentProofUpdateRequest(existingProofId, quotation.Total)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var order = await response.Content.ReadFromJsonAsync<OrderResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(order);
        Assert.Equal("PendingVerification", order.PaymentStatus);
        var proof = Assert.Single(order.PaymentProofs);
        Assert.Equal(existingProofId, proof.Id);
        Assert.Equal(firstProofFileId, proof.FileId);
        Assert.Equal(quotation.Total, proof.Amount);
    }

    // A pedido (2026-10-08): la pantalla de edición corrige la fecha del soporte junto con el
    // monto, en `updatedProofs[].paidOn`. La respuesta se lee como texto crudo, porque el contrato
    // es el nombre `paidOn` y la fecha ISO sin hora.
    [Fact]
    public async Task AddPaymentProofsCorrectsAnExistingProofPaidOnDate()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var proofFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var convert = await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest(
                null,
                [new OrderPaymentProofRequest(proofFileId, 10_000m, new DateOnly(2026, 10, 1))]),
            TestContext.Current.CancellationToken);
        var created = await ReadOrderAsync(convert);
        var existingProofId = Assert.Single(created.PaymentProofs).Id;

        var response = await client.PostAsJsonAsync(
            OrderProofsUrl(tenantId, created.Id),
            new AddOrderPaymentProofsRequest(
                [],
                null,
                [new OrderPaymentProofUpdateRequest(existingProofId, 12_000m, PaidOn: new DateOnly(2026, 10, 7))]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var json = JsonDocument.Parse(body);
        var proofJson = Assert.Single(json.RootElement.GetProperty("paymentProofs").EnumerateArray());
        Assert.Equal(12_000m, proofJson.GetProperty("amount").GetDecimal());
        Assert.Equal("2026-10-07", proofJson.GetProperty("paidOn").GetString());

        // `GET /orders/{id}` responde el detalle compuesto (`{ order, quotation }`), no un
        // OrderResponse: se lee por la cotización, como el resto de este archivo.
        var fetched = await GetOrderByQuotationAsync(client, tenantId, quotation.Id);
        Assert.NotNull(fetched);
        Assert.Equal(new DateOnly(2026, 10, 7), Assert.Single(fetched.PaymentProofs).PaidOn);
    }

    // Corregir un comprobante que no existe en este pedido -- de otro pedido, o un id inventado --
    // no puede pasar como si fuera valido.
    [Fact]
    public async Task AddPaymentProofsRejectsCorrectingAProofThatDoesNotExistOnThisOrder()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var created = await ReadOrderAsync(await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest(null, []),
            TestContext.Current.CancellationToken));

        var response = await client.PostAsJsonAsync(
            OrderProofsUrl(tenantId, created.Id),
            new AddOrderPaymentProofsRequest(
                [],
                UpdatedProofs: [new OrderPaymentProofUpdateRequest(Guid.CreateVersion7(), 1_000m)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    // Preparado, el pedido está congelado (spec 2026-11-10 §3.3): el paquete ya se armó con lo
    // que había en ese momento, y sumarle comprobantes ahí adentro cambiaría lo despachado.
    [Fact]
    public async Task AddPaymentProofsRejectsAPreparedOrder()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ApproverPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var firstProofFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var created = await ReadOrderAsync(await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest(
                null, [new OrderPaymentProofRequest(firstProofFileId, quotation.Total)]),
            TestContext.Current.CancellationToken));
        (await client.PostAsync(
            $"{OrderByIdUrl(tenantId, created.Id)}/prepare",
            null,
            TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        var proofFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var response = await client.PostAsJsonAsync(
            OrderProofsUrl(tenantId, created.Id),
            new AddOrderPaymentProofsRequest(
                [new OrderPaymentProofRequest(proofFileId, 10_000m)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("order.order.not_editable", body, StringComparison.Ordinal);
    }

    // Acciones de auditoría nuevas (spec 2026-09-14). Las filas viejas de audit.entries quedan como
    // están (D4); lo que se prueba es lo que se escribe desde ahora.
    [Fact]
    public async Task AddingProofsAndMarkingPaidAreAuditedAsOrderActions()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ApproverPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var created = await ReadOrderAsync(await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest(null, []),
            TestContext.Current.CancellationToken));
        var proofFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        (await client.PostAsJsonAsync(
            OrderProofsUrl(tenantId, created.Id),
            new AddOrderPaymentProofsRequest(
                [new OrderPaymentProofRequest(proofFileId, quotation.Total)]),
            TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        (await client.PostAsync(
            $"{OrderByIdUrl(tenantId, created.Id)}/mark-paid",
            null,
            TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        var actions = (await OutboxMessagesAsync(factory, "platform.audit.recorded.v1"))
            .Select(ActionOf)
            .ToArray();

        Assert.Contains("quotation.order.payment_proofs_added", actions);
        Assert.Contains("quotation.order.paid_marked", actions);
        Assert.DoesNotContain(actions, action => action.StartsWith("quotation.sale.", StringComparison.Ordinal));
    }

    // A pedido (2026-09-15): reemplazar el archivo de un comprobante mal cargado, junto con la
    // corrección de monto que ya existía como `UpdatedProofs`.
    [Fact]
    public async Task AddOrderPaymentProofsReplacesTheFileOfAnExistingProof()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var firstFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var convert = await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest(
                null, [new OrderPaymentProofRequest(firstFileId, quotation.Total)]),
            TestContext.Current.CancellationToken);
        var order = await ReadOrderAsync(convert);
        var proofId = Assert.Single(order.PaymentProofs).Id;
        var replacementFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);

        var response = await client.PostAsJsonAsync(
            OrderProofsUrl(tenantId, order.Id),
            new AddOrderPaymentProofsRequest(
                [],
                null,
                [new OrderPaymentProofUpdateRequest(proofId, quotation.Total, replacementFileId)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await ReadOrderAsync(response);
        var updatedProof = Assert.Single(updated.PaymentProofs);
        Assert.Equal(replacementFileId, updatedProof.FileId);
    }

    // A pedido (2026-09-15): quitar un comprobante cargado por error, distinto de corregirlo.
    [Fact]
    public async Task RemoveOrderPaymentProofRemovesAnExistingProof()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var firstFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var convert = await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest(
                null,
                [new OrderPaymentProofRequest(firstFileId, 10_000m)]),
            TestContext.Current.CancellationToken);
        var order = await ReadOrderAsync(convert);
        var proofId = Assert.Single(order.PaymentProofs).Id;

        var response = await client.DeleteAsync(
            $"{OrderProofsUrl(tenantId, order.Id)}/{proofId}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await ReadOrderAsync(response);
        Assert.Empty(updated.PaymentProofs);
        // Sin comprobantes, el pago vuelve a pendiente -- mismo cálculo que agregar/quitar un
        // producto.
        Assert.Equal("PaymentPending", updated.PaymentStatus);
    }

    [Fact]
    public async Task RemoveOrderPaymentProofRejectsAnUnknownProof()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var created = await ReadOrderAsync(await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest(null, []),
            TestContext.Current.CancellationToken));

        var response = await client.DeleteAsync(
            $"{OrderProofsUrl(tenantId, created.Id)}/{Guid.CreateVersion7()}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("order.payment_proof.not_found", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemoveOrderPaymentProofOnAPreparedOrderIsRejected()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ApproverPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var fileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var convert = await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest(
                null, [new OrderPaymentProofRequest(fileId, quotation.Total)]),
            TestContext.Current.CancellationToken);
        var order = await ReadOrderAsync(convert);
        var proofId = Assert.Single(order.PaymentProofs).Id;
        (await client.PostAsync(
            $"{OrderByIdUrl(tenantId, order.Id)}/prepare",
            null,
            TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        var response = await client.DeleteAsync(
            $"{OrderProofsUrl(tenantId, order.Id)}/{proofId}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("order.order.not_editable", body, StringComparison.Ordinal);
    }
}
