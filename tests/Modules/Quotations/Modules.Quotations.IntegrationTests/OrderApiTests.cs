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

/// <summary>US-13 a US-17: conversión de una cotización en pedido. Ya no hace falta haberla
/// enviado (a pedido, 2026-09). Las pruebas viven en las clases que heredan de esta, repartidas
/// por tema para que xUnit las corra en paralelo: dentro de una clase corren en serie.</summary>
public abstract class OrderApiTests
{
    // Sólo convertir y leer el pedido de una cotización cuelgan de ella: el pedido es un
    // sub-recurso suyo para esas dos cosas.
    private protected static string OrderUrl(Guid tenantId, Guid quotationId) =>
        $"{QuotationsUrl(tenantId)}/{quotationId}/order";

    // Todo lo que se le hace al pedido va por su propio id: quien llega desde el listado tiene
    // el id del pedido, no el de su cotización.
    private protected static string OrderByIdUrl(Guid tenantId, Guid orderId) =>
        $"/api/v1/tenants/{tenantId}/orders/{orderId}";

    private protected static string OrderProofsUrl(Guid tenantId, Guid orderId) =>
        $"{OrderByIdUrl(tenantId, orderId)}/proofs";

    private protected static string OrderItemsUrl(Guid tenantId, Guid orderId) =>
        $"{OrderByIdUrl(tenantId, orderId)}/items";

    private protected static string OrderCancelUrl(Guid tenantId, Guid orderId) =>
        $"{OrderByIdUrl(tenantId, orderId)}/cancel";

    // Spec 2026-09-16, decisión 5: anular exige su propio permiso; el resto de la siembra y de la
    // lectura sigue usando los de gestión.
    private protected static readonly string[] CancellerPermissions =
        [.. ManagerPermissions, OrdersPermissions.OrderCancel];

    private protected static string OrderInvoiceUrl(Guid tenantId, Guid orderId) =>
        $"{OrderByIdUrl(tenantId, orderId)}/invoice";

    private protected static string OrderUninvoiceUrl(Guid tenantId, Guid orderId) =>
        $"{OrderByIdUrl(tenantId, orderId)}/uninvoice";

    // Spec 2026-10-05, decisión 5: facturar y revertir exigen su propio permiso. Lleva también el de
    // aprobar porque sólo se factura un aprobado, y la prueba tiene que llegar hasta ahí.
    private protected static readonly string[] InvoicerPermissions =
        [.. ApproverPermissions, OrdersPermissions.OrderInvoice];

    internal static async Task<string> ConvertOneAsync(
        HttpClient client, QepApiFactory factory, Guid tenantId, Guid clientId, Guid productId)
    {
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var proofFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var response = await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest(
                "Pago verificado",
                [new OrderPaymentProofRequest(proofFileId, quotation.Total)]),
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var order = await response.Content.ReadConvertedOrderAsync(
            TestContext.Current.CancellationToken);
        Assert.NotNull(order);
        return order.OrderNumber;
    }

    /// <summary>El paso 2 del runbook del README, palabra por palabra: el UPSERT con GREATEST que
    /// fija el siguiente número sin poder retroceder.</summary>
    internal static async Task SetOrderCounterAsync(
        QepApiFactory factory, Guid tenantId, int year, long nextValue)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<QuotationsDbContext>();
        await dbContext.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO quotations.order_number_counters (tenant_id, year, next_value)
            VALUES ({tenantId}, {year}, {nextValue})
            ON CONFLICT (tenant_id, year) DO UPDATE
            SET next_value = GREATEST(quotations.order_number_counters.next_value, EXCLUDED.next_value)
            """,
            TestContext.Current.CancellationToken);
    }

    /// <summary>Un pedido "legado" para una cotización que siguió en Sent. Número fuera de la
    /// secuencia a propósito: lo único que tiene que chocar es el índice de la cotización.</summary>
    internal static async Task InsertLegacyOrderAsync(string connectionString, Guid tenantId, Guid quotationId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO quotations.orders (
                id, tenant_id, order_number, quotation_id, status, payment_status, notes, converted_at,
                converted_by, paid_marked_at, paid_marked_by, ritual_collection_sync_id, created_at, updated_at, version,
                client_id, advisor_id, currency, billing_company_id, billing_bank_name, billing_account_number,
                billing_account_currency)
            VALUES (
                @id, @tenantId, 'LEGADO-0001', @quotationId, 'Open', 'PaymentPending', NULL, now(),
                @convertedBy, NULL, NULL, NULL, now(), now(), 1,
                @clientId, @convertedBy, 'COP', @companyId, 'Bancolombia', '7542', 'COP')
            """,
            connection);
        command.Parameters.AddWithValue("id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("tenantId", tenantId);
        command.Parameters.AddWithValue("quotationId", quotationId);
        command.Parameters.AddWithValue("convertedBy", Guid.CreateVersion7());
        command.Parameters.AddWithValue("clientId", Guid.CreateVersion7());
        command.Parameters.AddWithValue("companyId", Guid.CreateVersion7());
        Assert.Equal(1, await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
    }

    // El pedido de una cotización: la ruta responde el detalle compuesto (spec 2026-10-15 §6.2), y lo
    // que estas pruebas miran es el pedido que viene adentro.
    private protected static async Task<OrderResponse> GetOrderByQuotationAsync(HttpClient client, Guid tenantId, Guid quotationId)
    {
        var detail = await client.GetFromJsonAsync<OrderDetailResponse>(
            OrderUrl(tenantId, quotationId), TestContext.Current.CancellationToken);
        Assert.NotNull(detail);
        return detail.Order;
    }

    private protected static async Task AssertDomainRejectionAsync(HttpResponseMessage response, string code)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var json = JsonDocument.Parse(body);
        Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
        Assert.False(json.RootElement.TryGetProperty("errors", out _));
    }

    // La conversión responde el detalle (spec 2026-10-15 §8) y el resto de las acciones, el pedido:
    // este ayudante lee las dos formas.
    private protected static async Task<OrderResponse> ReadOrderAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var element = json.RootElement.TryGetProperty("order", out var nested) ? nested : json.RootElement;
        var order = element.Deserialize<OrderResponse>(JsonSerializerOptions.Web);
        Assert.NotNull(order);
        return order;
    }

    /// <summary>Un pedido recién convertido sin comprobantes y marcado como pagado a mano: el punto de
    /// partida de facturar, que por defecto exige el pago completo (spec 2026-11-10 §6).</summary>
    private protected static async Task<OrderResponse> CreateApprovedOrderAsync(
        HttpClient client, QepApiFactory factory, Guid tenantId)
    {
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var created = await ReadOrderAsync(await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest(null, []),
            TestContext.Current.CancellationToken));

        return await ReadOrderAsync(await client.PostAsync(
            $"{OrderByIdUrl(tenantId, created.Id)}/mark-paid",
            content: null,
            TestContext.Current.CancellationToken));
    }

    private protected static string ActionOf(QuotationsOutboxMessage message)
    {
        using var payload = JsonDocument.Parse(message.PayloadJson);
        return payload.RootElement.GetProperty("action").GetString()!;
    }

    // El campo lo fija el AuditEventPayload de QuotationAuditPublisher (resourceId), que se
    // serializa con las opciones por defecto: el nombre viaja tal cual está declarado.
    private protected static string EntityIdOf(QuotationsOutboxMessage message)
    {
        using var payload = JsonDocument.Parse(message.PayloadJson);
        return payload.RootElement.GetProperty("resourceId").GetString()!;
    }

    /// <summary>Una edicion cualquiera sobre la cotizacion ya enviada: mueve UpdatedAt por
    /// delante de SentAt, que es lo unico que define "cambio despues de enviarse".</summary>
    private protected static async Task ChangeTheQuantityAsync(
        HttpClient client, Guid tenantId, QuotationResponse quotation)
    {
        var itemId = Assert.Single(quotation.Items).Id;
        var response = await client.PutAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/items/{itemId}",
            new UpdateQuotationItemRequest(2m),
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
