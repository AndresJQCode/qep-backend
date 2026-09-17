using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Quotations.Infrastructure.Persistence;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>La exportación de pedidos por correo: mismo contrato que cotizaciones, sobre el listado
/// de pedidos (SALE-01) y con `OrderRead`.</summary>
public sealed class OrderExportApiTests
{
    private static string OrdersUrl(Guid tenantId) => $"/api/v1/tenants/{tenantId}/orders";

    [Fact]
    public async Task ExportIsAcceptedAndLeavesAPendingOrdersJob()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, ownerUserId, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        await CreateOrderAsync(client, factory, tenantId);

        var response = await client.PostAsync(
            $"{OrdersUrl(tenantId)}/export?{CurrentRange()}", content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = await response.Content.ReadFromJsonAsync<AcceptedDto>(TestContext.Current.CancellationToken);
        var job = await FindExportJobAsync(factory, accepted!.JobId);
        Assert.Equal(ExportJobKind.Orders, job.Kind);
        Assert.Equal(ExportJobStatus.Pending, job.Status);
        Assert.Equal(ownerUserId, job.RequestedBy);
    }

    [Fact]
    public async Task ExportWithoutDatesIsUnprocessableWithTheFieldErrors()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;

        var response = await client.PostAsync(
            $"{OrdersUrl(tenantId)}/export", content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDto>(TestContext.Current.CancellationToken);
        Assert.Equal("validation.failed", problem?.Code);
        Assert.Contains("ConvertedFrom", problem!.Errors!.Keys);
        Assert.Contains("ConvertedTo", problem.Errors.Keys);
    }

    [Fact]
    public async Task ExportWithNoMatchingOrdersIsUnprocessable()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;

        var response = await client.PostAsync(
            $"{OrdersUrl(tenantId)}/export?{CurrentRange()}", content: null, TestContext.Current.CancellationToken);

        var problem = await response.Content.ReadFromJsonAsync<ProblemDto>(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("order.export.empty", problem?.Code);
    }

    // El cupo es por persona y cuenta los dos tipos: tres de cotizaciones frenan una de pedidos.
    [Fact]
    public async Task PendingQuotationExportsFillTheOrdersQuota()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        await CreateOrderAsync(client, factory, tenantId);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var quotationsExport =
            $"{QuotationsUrl(tenantId)}/export?createdFrom={Iso(today.AddDays(-7))}&createdTo={Iso(today.AddDays(1))}";
        for (var accepted = 0; accepted < 3; accepted++)
        {
            var ok = await client.PostAsync(quotationsExport, content: null, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Accepted, ok.StatusCode);
        }

        var response = await client.PostAsync(
            $"{OrdersUrl(tenantId)}/export?{CurrentRange()}", content: null, TestContext.Current.CancellationToken);

        var problem = await response.Content.ReadFromJsonAsync<ProblemDto>(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("order.export.pending_limit", problem?.Code);
    }

    [Fact]
    public async Task ExportWithoutTheOrderReadPermissionIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, QuotationsPermissions.QuotationRead);
        using var _ = client;

        var response = await client.PostAsync(
            $"{OrdersUrl(tenantId)}/export?{CurrentRange()}", content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ExportForAnotherTenantIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, owner) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = owner;
        var (_, _, otherOwner) = await RegisterTenantAsync(factory, OrdersPermissions.OrderRead);
        using var __ = otherOwner;

        var response = await otherOwner.PostAsync(
            $"{OrdersUrl(tenantId)}/export?{CurrentRange()}", content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // De punta a punta: el POST encola, un tick arma el Excel con las filas del listado de pedidos
    // en el orden de su tabla, y el correo sale.
    [Fact]
    public async Task TheWorkerTurnsTheRequestIntoTheOrdersWorkbookAndTheEmail()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, ownerUserId, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        await CreateOrderAsync(client, factory, tenantId);
        await CreateOrderAsync(client, factory, tenantId);

        var response = await client.PostAsync(
            $"{OrdersUrl(tenantId)}/export?{CurrentRange()}", content: null, TestContext.Current.CancellationToken);
        var accepted = await response.Content.ReadFromJsonAsync<AcceptedDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(accepted);

        Assert.Equal(ExportJobRunOutcome.Completed, await RunExportJobAsync(factory));

        var job = await FindExportJobAsync(factory, accepted.JobId);
        Assert.Equal(2, job.RowCount);
        Assert.Matches(@"^pedidos-\d{4}-\d{2}-\d{2}-\d{4}\.xlsx$", job.FileName);
        var ready = Assert.Single(await OutboxMessagesAsync(factory, "quotations.export-ready.v1"));
        using (var payload = JsonDocument.Parse(ready.PayloadJson))
        {
            Assert.Equal("Orders", payload.RootElement.GetProperty("kind").GetString());
        }

        var sheet = ExportWorkbookReader.Read(await factory.ObjectStorage.DownloadAsync(
            $"exports/tenants/{tenantId:N}/jobs/{accepted.JobId:N}.xlsx", TestContext.Current.CancellationToken));
        var list = await client.GetFromJsonAsync<OrdersPageResponse>(
            $"{OrdersUrl(tenantId)}?{CurrentRange()}", TestContext.Current.CancellationToken);
        var items = list!.Items.ToArray();
        Assert.Equal("Pedidos", sheet.Name);
        Assert.Equal(
            ["Pedido", "Cliente", "Asesor", "Fecha", "Pago", "Estado", "Moneda", "Total",
                "Comprobantes", "Comprobante 1", "Comprobante 2", "Comprobante 3"],
            sheet.Rows[0]);
        Assert.Equal(items.Select(item => item.OrderNumber), sheet.Rows.Skip(1).Select(row => row[0]));
        var first = sheet.Rows[1];
        Assert.Equal(items[0].ClientName, first[1]);
        Assert.Equal(items[0].AdvisorName ?? string.Empty, first[2]);
        // La fecha sale en la hora del tenant, al minuto y sin offset (spec 2026-09-17, punto 8a).
        Assert.Equal(LocalMinuteInBogota(items[0].ConvertedAt), first[3]);
        // La API sigue mandando el enum (A8); el archivo, la etiqueta de la tabla (A7).
        Assert.Equal("Pending", items[0].Status);
        Assert.Equal(items[0].PaymentMethod ?? "Pago pendiente", first[4]);
        Assert.Equal("Pendiente", first[5]);
        Assert.Equal(items[0].Currency, first[6]);
        Assert.True(sheet.NumericCells[1][7]);
        Assert.Equal(items[0].Total, decimal.Parse(first[7], CultureInfo.InvariantCulture));
        // Sin comprobantes (spec 2026-09-15, E2): la cantidad en cero y las tres celdas vacías, que
        // igual salen (E7).
        Assert.True(sheet.NumericCells[1][8]);
        Assert.Equal("0", first[8]);
        Assert.Equal([string.Empty, string.Empty, string.Empty], first.Skip(9));

        Assert.Equal("Sent", await WaitForEmailStatusAsync(
            database.GetConnectionString(), ownerUserId, "quotations.export-ready.v1"));
    }

    // Keyset y no offset (D8, hallazgo 11), sobre el orden del listado de pedidos. Mismo diseño que
    // la de cotizaciones: de a una fila, un pedido convertido entre el lote 1 y el 2 (con offset,
    // se repetiría la del borde) y uno ya leído que sale del filtro entre el 2 y el 3 (con offset,
    // se saltaría una).
    [Fact]
    public async Task OrdersCreatedOrLeavingTheFilterBetweenBatchesNeitherRepeatNorSkip()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        for (var converted = 0; converted < 3; converted++)
        {
            await CreateOrderAsync(client, factory, tenantId);
        }

        var expected = (await ReadPendingBatchAsync(factory, tenantId, after: null, limit: 100))
            .Select(row => row.Order.Id)
            .ToArray();
        Assert.Equal(3, expected.Length);

        var first = await ReadPendingBatchAsync(factory, tenantId, after: null, limit: 1);
        await CreateOrderAsync(client, factory, tenantId);
        var second = await ReadPendingBatchAsync(factory, tenantId, CursorOf(first), limit: 1);
        await SetOrderStatusAsync(factory, second.Single().Order.Id, OrderStatus.Approved);
        var third = await ReadPendingBatchAsync(factory, tenantId, CursorOf(second), limit: 1);
        var fourth = await ReadPendingBatchAsync(factory, tenantId, CursorOf(third), limit: 1);

        Assert.Equal(expected, first.Concat(second).Concat(third).Select(row => row.Order.Id));
        Assert.Empty(fourth);
    }

    // El repositorio real, contra Postgres. `Pending` es el filtro que el pedido aprobado abandona.
    private static async Task<IReadOnlyList<OrderWithQuotation>> ReadPendingBatchAsync(
        QepApiFactory factory, Guid tenantId, OrderExportCursor? after, int limit)
    {
        // El repositorio recibe instantes desde la spec 2026-09-17 (punto 3): la ventana es amplia a
        // propósito, lo que se prueba es el keyset.
        var now = DateTimeOffset.UtcNow;
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IOrderRepository>().ListForExportAsync(
            tenantId,
            clientId: null,
            clientIds: null,
            advisorId: null,
            OrderStatus.Pending,
            paymentStatus: null,
            now.AddDays(-7),
            now.AddDays(2),
            orderNumber: null,
            after,
            limit,
            TestContext.Current.CancellationToken);
    }

    private static OrderExportCursor CursorOf(IReadOnlyList<OrderWithQuotation> batch) =>
        new(batch[^1].Order.ConvertedAt, batch[^1].Order.OrderNumber);

    // Directo en la base: lo que se prueba es la lectura, no la aprobación.
    private static async Task SetOrderStatusAsync(QepApiFactory factory, OrderId orderId, OrderStatus status)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<QuotationsDbContext>();
        var updated = await dbContext.Orders
            .Where(order => order.Id == orderId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(order => order.Status, status),
                TestContext.Current.CancellationToken);
        Assert.Equal(1, updated);
    }

    // El desempate del keyset (D8) sobre el orden de pedidos, contra Postgres: tres pedidos con el
    // mismo instante de conversión, leídos de a uno hasta que no queda nada. Si el corte no mirara
    // el número, o lo comparara con <= en vez de <, el lote siguiente saltaría las otras dos o
    // repetiría la misma.
    [Fact]
    public async Task OrdersThatShareTheConversionInstantComeOutOnceEachByNumber()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        for (var converted = 0; converted < 3; converted++)
        {
            await CreateOrderAsync(client, factory, tenantId);
        }

        await TieConversionInstantAsync(factory, tenantId, DateTimeOffset.UtcNow.AddHours(-1));
        var expected = (await ReadPendingBatchAsync(factory, tenantId, after: null, limit: 100))
            .Select(row => row.Order.OrderNumber)
            .OrderDescending(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(3, expected.Length);

        var read = new List<string>();
        OrderExportCursor? after = null;
        // Con tope: un corte con <= devolvería la misma fila para siempre.
        for (var batch = 0; batch < 10; batch++)
        {
            var rows = await ReadPendingBatchAsync(factory, tenantId, after, limit: 1);
            if (rows.Count == 0)
            {
                break;
            }

            read.Add(rows.Single().Order.OrderNumber);
            after = CursorOf(rows);
        }

        Assert.Equal(expected, read);
    }

    // Las tres del tenant al mismo instante, directo en la base: la conversión pone la fecha sola.
    private static async Task TieConversionInstantAsync(QepApiFactory factory, Guid tenantId, DateTimeOffset convertedAt)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<QuotationsDbContext>();
        var updated = await dbContext.Orders
            .Where(order => order.TenantId == tenantId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(order => order.ConvertedAt, convertedAt),
                TestContext.Current.CancellationToken);
        Assert.Equal(3, updated);
    }

    private static string CurrentRange()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return $"convertedFrom={Iso(today.AddDays(-7))}&convertedTo={Iso(today.AddDays(1))}";
    }

    private static string Iso(DateOnly date) =>
        date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Un pedido convertido hoy, sin comprobantes (pago pendiente), mismo camino que
    /// OrderListApiTests.ConvertToOrderAsync.</summary>
    private static async Task<OrderResponse> CreateOrderAsync(HttpClient client, QepApiFactory factory, Guid tenantId)
    {
        var customerId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, customerId, productId);
        var response = await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/order",
            new ConvertQuotationToOrderRequest("PaymentPending", null, []),
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var order = await response.Content.ReadFromJsonAsync<OrderResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(order);
        return order;
    }

    // Spec 2026-09-15, de punta a punta con la opción encendida: el comprobante que se subió primero
    // es el enlace «Ver» a su copia pública, uno privado dice «Sin enlace» y el tercero queda vacío.
    // El privado se simula borrando su clave en la base: es lo que tienen los comprobantes de antes
    // de la opción (P8).
    [Fact]
    public async Task TheOrdersWorkbookLinksEachPublicProof()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), publicPaymentProofLinks: true);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var order = await CreateOrderWithProofsAsync(client, factory, tenantId, proofCount: 1);
        var firstProofId = Assert.Single(order.PaymentProofs).Id;
        var withSecond = await AddProofAsync(client, factory, tenantId, order.QuotationId);
        var secondProofId = Assert.Single(withSecond.PaymentProofs, proof => proof.Id != firstProofId).Id;
        await ClearPublicStorageKeyAsync(factory, secondProofId);
        var firstKey = await PublicStorageKeyOfAsync(factory, firstProofId);

        var response = await client.PostAsync(
            $"{OrdersUrl(tenantId)}/export?{CurrentRange()}", content: null, TestContext.Current.CancellationToken);
        var accepted = await response.Content.ReadFromJsonAsync<AcceptedDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(accepted);
        Assert.Equal(ExportJobRunOutcome.Completed, await RunExportJobAsync(factory));

        var sheet = ExportWorkbookReader.Read(await factory.ObjectStorage.DownloadAsync(
            $"exports/tenants/{tenantId:N}/jobs/{accepted.JobId:N}.xlsx", TestContext.Current.CancellationToken));
        Assert.Equal(
            ["Comprobantes", "Comprobante 1", "Comprobante 2", "Comprobante 3"],
            sheet.Rows[0].Skip(8));
        var row = sheet.Rows[1];
        Assert.Equal(order.OrderNumber, row[0]);
        Assert.True(sheet.NumericCells[1][8]);
        Assert.Equal("2", row[8]);
        Assert.Equal(
            $"HYPERLINK(\"{InMemoryPublicObjectStorage.BaseUrl}/{firstKey}\",\"Ver\")",
            sheet.Formulas[1][9]);
        Assert.Equal("Ver", row[9]);
        Assert.Null(sheet.Formulas[1][10]);
        Assert.Equal("Sin enlace", row[10]);
        Assert.Equal(string.Empty, row[11]);
    }

    // E6 contra Postgres: una sola lectura por lote, por pedido y en el orden de las columnas —fecha de
    // subida, y el id como desempate entre los que llegaron en el mismo request—, y sólo del tenant: la
    // tabla de comprobantes no tiene tenant y el filtro va por el join con `orders`. Un pedido sin
    // comprobantes no aparece.
    [Fact]
    public async Task PaymentProofsForTheExportComeInUploadOrderAndOnlyFromTheTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        var (otherTenantId, _, otherClient) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        using var __ = otherClient;
        var order = await CreateOrderWithProofsAsync(client, factory, tenantId, proofCount: 2);
        var withThird = await AddProofAsync(client, factory, tenantId, order.QuotationId);
        var withoutProofs = await CreateOrderAsync(client, factory, tenantId);
        var otherOrder = await CreateOrderWithProofsAsync(otherClient, factory, otherTenantId, proofCount: 1);

        await using var scope = factory.Services.CreateAsyncScope();
        var proofs = await scope.ServiceProvider.GetRequiredService<IOrderRepository>()
            .ListPaymentProofsForExportAsync(
                tenantId,
                [new OrderId(order.Id), new OrderId(withoutProofs.Id), new OrderId(otherOrder.Id)],
                TestContext.Current.CancellationToken);

        var read = Assert.Single(proofs);
        Assert.Equal(new OrderId(order.Id), read.Key);
        // Los dos de la conversión comparten la fecha de subida: los ordena el id, que Postgres
        // compara como el texto canónico del uuid.
        var sameRequest = order.PaymentProofs
            .Select(proof => proof.Id)
            .OrderBy(id => id.ToString("D", CultureInfo.InvariantCulture), StringComparer.Ordinal);
        var added = Assert.Single(
            withThird.PaymentProofs, proof => order.PaymentProofs.All(existing => existing.Id != proof.Id)).Id;
        Assert.Equal(sameRequest.Append(added), read.Value.Select(proof => proof.Id.Value));
    }

    /// <summary>Un pedido convertido hoy con <paramref name="proofCount"/> comprobantes (pago
    /// parcial), en un mismo request: comparten la fecha de subida.</summary>
    private static async Task<OrderResponse> CreateOrderWithProofsAsync(
        HttpClient client, QepApiFactory factory, Guid tenantId, int proofCount)
    {
        var customerId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, customerId, productId);
        var proofs = new List<OrderPaymentProofRequest>();
        for (var index = 0; index < proofCount; index++)
        {
            proofs.Add(new OrderPaymentProofRequest(
                await CreateAvailablePaymentProofFileAsync(client, factory, tenantId), 10_000m));
        }

        var response = await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/order",
            new ConvertQuotationToOrderRequest("PartialPaymentReceived", null, proofs),
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var order = await response.Content.ReadFromJsonAsync<OrderResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(order);
        return order;
    }

    /// <summary>Suma un comprobante a un pedido pendiente: su fecha de subida es posterior a la de
    /// los que ya tenía.</summary>
    private static async Task<OrderResponse> AddProofAsync(
        HttpClient client, QepApiFactory factory, Guid tenantId, Guid quotationId)
    {
        var fileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var response = await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotationId}/order/proofs",
            new AddOrderPaymentProofsRequest(
                "PartialPaymentReceived", [new OrderPaymentProofRequest(fileId, 5_000m)]),
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var order = await response.Content.ReadFromJsonAsync<OrderResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(order);
        return order;
    }

    // Directo en la base: así queda un comprobante de antes de la opción (P8), sin copia pública.
    private static async Task ClearPublicStorageKeyAsync(QepApiFactory factory, Guid proofId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<QuotationsDbContext>();
        var id = new OrderPaymentProofId(proofId);
        var updated = await dbContext.OrderPaymentProofs
            .Where(proof => proof.Id == id)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(proof => proof.PublicStorageKey, (string?)null),
                TestContext.Current.CancellationToken);
        Assert.Equal(1, updated);
    }

    private static async Task<string> PublicStorageKeyOfAsync(QepApiFactory factory, Guid proofId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<QuotationsDbContext>();
        var id = new OrderPaymentProofId(proofId);
        var key = await dbContext.OrderPaymentProofs
            .AsNoTracking()
            .Where(proof => proof.Id == id)
            .Select(proof => proof.PublicStorageKey)
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(key);
        return key;
    }

    private sealed record AcceptedDto(Guid JobId, DateTimeOffset RequestedAt);

    private sealed record ProblemDto(string? Code, Dictionary<string, string[]>? Errors);
}
