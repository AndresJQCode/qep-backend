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
        var today = TodayInBogota();
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

    // De punta a punta: el POST encola, un tick arma el Excel del ERP contable (ajuste
    // 2026-09-20) con las columnas y el correo sale. Cada pedido de CreateOrderAsync tiene una
    // sola línea, así que filas de archivo y pedidos leídos coinciden.
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
            [
                "EMPRESA", "Forma de pago 1", "V. Consignacion 1", "Cod. Producto", "U.Medida",
                "Cantidad", "Valor Unit", "IVA", "Descuento", "Nota Detalle",
                "Fecha Pago 1", "Fecha Pago 2", "Fecha Pago 3", "Fecha Pago 4", "Fecha Pago 5",
                "Ciudad", "Documento", "Pedido", "Direccion", "Observaciones", "Telefono", "Email",
                "Cod. Asesor", "Banco", "Cuenta",
                "V. Comprobante 1", "URL Comprobante 1", "V. Comprobante 2", "URL Comprobante 2",
                "V. Comprobante 3", "URL Comprobante 3", "V. Comprobante 4", "URL Comprobante 4",
                "V. Comprobante 5", "URL Comprobante 5",
            ],
            sheet.Rows[0]);
        Assert.Equal(items.Select(item => item.OrderNumber), sheet.Rows.Skip(1).Select(row => row[17]));
        var first = sheet.Rows[1];
        // CreateCompanyWithBankAccountAsync siempre da de alta la misma razón social.
        Assert.Equal("QEP Comercial S.A.S.", first[0]);
        Assert.Equal(items[0].PaymentMethod, first[1]);
        Assert.True(sheet.NumericCells[1][2]);
        Assert.Equal(items[0].Total, decimal.Parse(first[2], CultureInfo.InvariantCulture));
        // La única línea del pedido: cantidad 1 de un producto sin tasa de impuesto, en la
        // primera escala (1-9, sin descuento, múltiplo de 1).
        Assert.NotEqual(string.Empty, first[3]);
        Assert.Equal("Múltiplo de 1", first[4]);
        Assert.True(sheet.NumericCells[1][5]);
        Assert.Equal(1m, decimal.Parse(first[5], CultureInfo.InvariantCulture));
        Assert.True(sheet.NumericCells[1][6]);
        Assert.Equal(100_000m, decimal.Parse(first[6], CultureInfo.InvariantCulture));
        Assert.True(sheet.NumericCells[1][7]);
        Assert.Equal(0m, decimal.Parse(first[7], CultureInfo.InvariantCulture));
        Assert.True(sheet.NumericCells[1][8]);
        Assert.Equal(0m, decimal.Parse(first[8], CultureInfo.InvariantCulture));
        Assert.Equal(string.Empty, first[9]);
        // Sin comprobantes: las cinco fechas de pago quedan vacías.
        Assert.Equal(
            [string.Empty, string.Empty, string.Empty, string.Empty, string.Empty],
            first.Skip(10).Take(5));
        // Sin parte de entrega propia: "los mismos datos del cliente" (CreateActiveCustomerAsync).
        Assert.NotEqual(string.Empty, first[15]);
        Assert.NotEqual(string.Empty, first[16]);
        Assert.Equal(items[0].OrderNumber, first[17]);
        Assert.Equal("Calle 10 # 45-12", first[18]);
        Assert.Equal("310 935 2187", first[20]);
        Assert.Equal("compras@verde.co", first[21]);
        // El owner nace sin código (CreateActive): la celda sale vacía.
        Assert.Equal(string.Empty, first[22]);
        // Banco y Cuenta: la cuenta de facturación de CreateCompanyWithBankAccountAsync, siempre en
        // Bancolombia y con un número al azar.
        Assert.Equal("Bancolombia", first[23]);
        Assert.NotEqual(string.Empty, first[24]);
        // Sin comprobantes: los cinco pares de monto y enlace quedan vacíos.
        Assert.All(first.Skip(25).Take(10), cell => Assert.Equal(string.Empty, cell));

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
        var today = TodayInBogota();
        return $"convertedFrom={Iso(today.AddDays(-7))}&convertedTo={Iso(today.AddDays(1))}";
    }

    private static string Iso(DateOnly date) =>
        date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // Spec 2026-09-24, D9, de punta a punta: el código se carga por PUT .../profile en Tenancy, el
    // adaptador de Bootstrapper lo resuelve desde la membresía, y sale como número en "Cod.
    // Asesor", después de Email. Ninguna prueba unitaria ve ese cruce.
    [Fact]
    public async Task TheOrdersWorkbookCarriesTheAdvisorsCodeAfterEmail()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        // X-Permissions reemplaza el set por defecto del stub, así que los de Tenancy para leer el
        // roster y editar el perfil se piden explícitos.
        var (tenantId, _, client) = await RegisterTenantAsync(
            factory, [.. ManagerPermissions, "advisorship.read", "advisorship.manage"]);
        using var _ = client;
        await CreateOrderAsync(client, factory, tenantId);
        await SetOwnerAdvisorCodeAsync(client, tenantId, 7);

        var response = await client.PostAsync(
            $"{OrdersUrl(tenantId)}/export?{CurrentRange()}", content: null, TestContext.Current.CancellationToken);
        var accepted = await response.Content.ReadFromJsonAsync<AcceptedDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(accepted);
        Assert.Equal(ExportJobRunOutcome.Completed, await RunExportJobAsync(factory));

        var sheet = ExportWorkbookReader.Read(await factory.ObjectStorage.DownloadAsync(
            $"exports/tenants/{tenantId:N}/jobs/{accepted.JobId:N}.xlsx", TestContext.Current.CancellationToken));
        Assert.Equal("Cod. Asesor", sheet.Rows[0][22]);
        Assert.Equal("7", sheet.Rows[1][22]);
        Assert.True(sheet.NumericCells[1][22]);
    }

    // La asesora de CreateOrderAsync es el owner, la única membresía del tenant recién registrado.
    // La versión se lee del roster en vez de suponerla: If-Match tiene que llevar la vigente.
    private static async Task SetOwnerAdvisorCodeAsync(HttpClient client, Guid tenantId, int advisorCode)
    {
        var roster = await client.GetFromJsonAsync<MembershipRosterPayload>(
            $"/api/v1/tenants/{tenantId}/memberships", TestContext.Current.CancellationToken);
        var owner = Assert.Single(roster!.Items, item => item.IsOwner);
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"/api/v1/tenants/{tenantId}/memberships/{owner.Id}/profile")
        {
            Content = JsonContent.Create(new { displayName = "Laura Gómez", advisorCode })
        };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{owner.Version}\"");
        using var updated = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
    }

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

    // De punta a punta (ajuste 2026-09-20): dos comprobantes de un mismo pedido llenan "Fecha Pago
    // 1" y "Fecha Pago 2" en el orden en que se subieron, y las tres columnas restantes quedan
    // vacías. Desde el 2026-09-24 también llenan "V. Comprobante N" y "URL Comprobante N", al final
    // de la hoja, en el mismo orden.
    [Fact]
    public async Task TheOrdersWorkbookFillsAPaymentDatePerProofInUploadOrder()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var order = await CreateOrderWithProofsAsync(client, factory, tenantId, proofCount: 1);
        var withSecond = await AddProofAsync(client, factory, tenantId, order.Id);

        var response = await client.PostAsync(
            $"{OrdersUrl(tenantId)}/export?{CurrentRange()}", content: null, TestContext.Current.CancellationToken);
        var accepted = await response.Content.ReadFromJsonAsync<AcceptedDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(accepted);
        Assert.Equal(ExportJobRunOutcome.Completed, await RunExportJobAsync(factory));

        var sheet = ExportWorkbookReader.Read(await factory.ObjectStorage.DownloadAsync(
            $"exports/tenants/{tenantId:N}/jobs/{accepted.JobId:N}.xlsx", TestContext.Current.CancellationToken));
        Assert.Equal(
            ["Fecha Pago 1", "Fecha Pago 2", "Fecha Pago 3", "Fecha Pago 4", "Fecha Pago 5"],
            sheet.Rows[0].Skip(10).Take(5));
        var row = sheet.Rows[1];
        Assert.Equal(order.OrderNumber, row[17]);
        Assert.NotEqual(string.Empty, row[10]);
        Assert.NotEqual(string.Empty, row[11]);
        Assert.Equal([string.Empty, string.Empty, string.Empty], row.Skip(12).Take(3));
        // El segundo comprobante se subió después: su fecha no puede ser anterior a la del
        // primero. Comparables como texto porque el formato es "yyyy-MM-dd HH:mm".
        Assert.True(string.CompareOrdinal(row[10], row[11]) <= 0);
        Assert.NotEmpty(withSecond.PaymentProofs);
        // 2026-09-24: el monto de cada comprobante, en el mismo orden, como número. Con los enlaces
        // públicos apagados (el default de la factoría) los dos dicen «Sin enlace».
        Assert.Equal(
            ["V. Comprobante 1", "URL Comprobante 1", "V. Comprobante 2", "URL Comprobante 2"],
            sheet.Rows[0].Skip(25).Take(4));
        Assert.Equal(10_000m, decimal.Parse(row[25], CultureInfo.InvariantCulture));
        Assert.True(sheet.NumericCells[1][25]);
        Assert.Equal(OrdersExportProcessor.PrivateProofText, row[26]);
        Assert.Equal(5_000m, decimal.Parse(row[27], CultureInfo.InvariantCulture));
        Assert.Equal(OrdersExportProcessor.PrivateProofText, row[28]);
        Assert.All(row.Skip(29).Take(6), cell => Assert.Equal(string.Empty, cell));
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
        var withThird = await AddProofAsync(client, factory, tenantId, order.Id);
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
        HttpClient client, QepApiFactory factory, Guid tenantId, Guid orderId)
    {
        var fileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/orders/{orderId}/proofs",
            new AddOrderPaymentProofsRequest(
                "PartialPaymentReceived", [new OrderPaymentProofRequest(fileId, 5_000m)]),
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var order = await response.Content.ReadFromJsonAsync<OrderResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(order);
        return order;
    }

    private sealed record AcceptedDto(Guid JobId, DateTimeOffset RequestedAt);

    private sealed record ProblemDto(string? Code, Dictionary<string, string[]>? Errors);

    private sealed record MembershipRosterPayload(IReadOnlyList<MembershipRosterRowPayload> Items);

    private sealed record MembershipRosterRowPayload(Guid Id, long Version, bool IsOwner);
}
