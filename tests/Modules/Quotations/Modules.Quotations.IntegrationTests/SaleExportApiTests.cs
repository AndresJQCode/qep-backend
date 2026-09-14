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

/// <summary>La exportación de ventas por correo: mismo contrato que cotizaciones, sobre el listado
/// de ventas (SALE-01) y con `SaleRead`.</summary>
public sealed class SaleExportApiTests
{
    private static string SalesUrl(Guid tenantId) => $"/api/v1/tenants/{tenantId}/sales";

    [Fact]
    public async Task ExportIsAcceptedAndLeavesAPendingSalesJob()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, ownerUserId, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        await CreateSaleAsync(client, factory, tenantId);

        var response = await client.PostAsync(
            $"{SalesUrl(tenantId)}/export?{CurrentRange()}", content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = await response.Content.ReadFromJsonAsync<AcceptedDto>(TestContext.Current.CancellationToken);
        var job = await FindExportJobAsync(factory, accepted!.JobId);
        Assert.Equal(ExportJobKind.Sales, job.Kind);
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
            $"{SalesUrl(tenantId)}/export", content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDto>(TestContext.Current.CancellationToken);
        Assert.Equal("validation.failed", problem?.Code);
        Assert.Contains("ConvertedFrom", problem!.Errors!.Keys);
        Assert.Contains("ConvertedTo", problem.Errors.Keys);
    }

    [Fact]
    public async Task ExportWithNoMatchingSalesIsUnprocessable()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;

        var response = await client.PostAsync(
            $"{SalesUrl(tenantId)}/export?{CurrentRange()}", content: null, TestContext.Current.CancellationToken);

        var problem = await response.Content.ReadFromJsonAsync<ProblemDto>(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("sale.export.empty", problem?.Code);
    }

    // El cupo es por persona y cuenta los dos tipos: tres de cotizaciones frenan una de ventas.
    [Fact]
    public async Task PendingQuotationExportsFillTheSalesQuota()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        await CreateSaleAsync(client, factory, tenantId);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var quotationsExport =
            $"{QuotationsUrl(tenantId)}/export?createdFrom={Iso(today.AddDays(-7))}&createdTo={Iso(today.AddDays(1))}";
        for (var accepted = 0; accepted < 3; accepted++)
        {
            var ok = await client.PostAsync(quotationsExport, content: null, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Accepted, ok.StatusCode);
        }

        var response = await client.PostAsync(
            $"{SalesUrl(tenantId)}/export?{CurrentRange()}", content: null, TestContext.Current.CancellationToken);

        var problem = await response.Content.ReadFromJsonAsync<ProblemDto>(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("sale.export.pending_limit", problem?.Code);
    }

    [Fact]
    public async Task ExportWithoutTheSaleReadPermissionIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, QuotationsPermissions.QuotationRead);
        using var _ = client;

        var response = await client.PostAsync(
            $"{SalesUrl(tenantId)}/export?{CurrentRange()}", content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ExportForAnotherTenantIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, owner) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = owner;
        var (_, _, otherOwner) = await RegisterTenantAsync(factory, SalesPermissions.SaleRead);
        using var __ = otherOwner;

        var response = await otherOwner.PostAsync(
            $"{SalesUrl(tenantId)}/export?{CurrentRange()}", content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // De punta a punta: el POST encola, un tick arma el Excel con las filas del listado de ventas
    // en el orden de su tabla, y el correo sale.
    [Fact]
    public async Task TheWorkerTurnsTheRequestIntoTheSalesWorkbookAndTheEmail()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, ownerUserId, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        await CreateSaleAsync(client, factory, tenantId);
        await CreateSaleAsync(client, factory, tenantId);

        var response = await client.PostAsync(
            $"{SalesUrl(tenantId)}/export?{CurrentRange()}", content: null, TestContext.Current.CancellationToken);
        var accepted = await response.Content.ReadFromJsonAsync<AcceptedDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(accepted);

        Assert.Equal(ExportJobRunOutcome.Completed, await RunExportJobAsync(factory));

        var job = await FindExportJobAsync(factory, accepted.JobId);
        Assert.Equal(2, job.RowCount);
        Assert.Matches(@"^ventas-\d{4}-\d{2}-\d{2}-\d{4}\.xlsx$", job.FileName);
        var ready = Assert.Single(await OutboxMessagesAsync(factory, "quotations.export-ready.v1"));
        using (var payload = JsonDocument.Parse(ready.PayloadJson))
        {
            Assert.Equal("Sales", payload.RootElement.GetProperty("kind").GetString());
        }

        var sheet = ExportWorkbookReader.Read(await factory.ObjectStorage.DownloadAsync(
            $"exports/tenants/{tenantId:N}/jobs/{accepted.JobId:N}.xlsx", TestContext.Current.CancellationToken));
        var list = await client.GetFromJsonAsync<SalesPageResponse>(
            $"{SalesUrl(tenantId)}?{CurrentRange()}", TestContext.Current.CancellationToken);
        var items = list!.Items.ToArray();
        Assert.Equal("Ventas", sheet.Name);
        Assert.Equal(["Venta", "Cliente", "Asesor", "Fecha", "Pago", "Estado", "Moneda", "Total"], sheet.Rows[0]);
        Assert.Equal(items.Select(item => item.SaleNumber), sheet.Rows.Skip(1).Select(row => row[0]));
        var first = sheet.Rows[1];
        Assert.Equal(items[0].ClientName, first[1]);
        Assert.Equal(items[0].AdvisorEmail ?? string.Empty, first[2]);
        Assert.Equal(items[0].ConvertedAt, DateTimeOffset.Parse(first[3], CultureInfo.InvariantCulture));
        // La API sigue mandando el enum (A8); el archivo, la etiqueta de la tabla (A7).
        Assert.Equal("Pending", items[0].Status);
        Assert.Equal(items[0].PaymentMethod ?? "Pago pendiente", first[4]);
        Assert.Equal("Pendiente", first[5]);
        Assert.Equal(items[0].Currency, first[6]);
        Assert.True(sheet.NumericCells[1][7]);
        Assert.Equal(items[0].Total, decimal.Parse(first[7], CultureInfo.InvariantCulture));

        Assert.Equal("Sent", await WaitForEmailStatusAsync(
            database.GetConnectionString(), ownerUserId, "quotations.export-ready.v1"));
    }

    // Keyset y no offset (D8, hallazgo 11), sobre el orden del listado de ventas. Mismo diseño que
    // la de cotizaciones: de a una fila, una venta convertida entre el lote 1 y el 2 (con offset,
    // se repetiría la del borde) y una ya leída que sale del filtro entre el 2 y el 3 (con offset,
    // se saltaría una).
    [Fact]
    public async Task SalesCreatedOrLeavingTheFilterBetweenBatchesNeitherRepeatNorSkip()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        for (var converted = 0; converted < 3; converted++)
        {
            await CreateSaleAsync(client, factory, tenantId);
        }

        var expected = (await ReadPendingBatchAsync(factory, tenantId, after: null, limit: 100))
            .Select(row => row.Sale.Id)
            .ToArray();
        Assert.Equal(3, expected.Length);

        var first = await ReadPendingBatchAsync(factory, tenantId, after: null, limit: 1);
        await CreateSaleAsync(client, factory, tenantId);
        var second = await ReadPendingBatchAsync(factory, tenantId, CursorOf(first), limit: 1);
        await SetSaleStatusAsync(factory, second.Single().Sale.Id, SaleStatus.Approved);
        var third = await ReadPendingBatchAsync(factory, tenantId, CursorOf(second), limit: 1);
        var fourth = await ReadPendingBatchAsync(factory, tenantId, CursorOf(third), limit: 1);

        Assert.Equal(expected, first.Concat(second).Concat(third).Select(row => row.Sale.Id));
        Assert.Empty(fourth);
    }

    // El repositorio real, contra Postgres. `Pending` es el filtro que la venta aprobada abandona.
    private static async Task<IReadOnlyList<SaleWithQuotation>> ReadPendingBatchAsync(
        QepApiFactory factory, Guid tenantId, SaleExportCursor? after, int limit)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISaleRepository>().ListForExportAsync(
            tenantId,
            clientId: null,
            clientIds: null,
            advisorId: null,
            SaleStatus.Pending,
            paymentStatus: null,
            today.AddDays(-7),
            today.AddDays(1),
            saleNumber: null,
            after,
            limit,
            TestContext.Current.CancellationToken);
    }

    private static SaleExportCursor CursorOf(IReadOnlyList<SaleWithQuotation> batch) =>
        new(batch[^1].Sale.ConvertedAt, batch[^1].Sale.SaleNumber);

    // Directo en la base: lo que se prueba es la lectura, no la aprobación.
    private static async Task SetSaleStatusAsync(QepApiFactory factory, SaleId saleId, SaleStatus status)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<QuotationsDbContext>();
        var updated = await dbContext.Sales
            .Where(sale => sale.Id == saleId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(sale => sale.Status, status),
                TestContext.Current.CancellationToken);
        Assert.Equal(1, updated);
    }

    // El desempate del keyset (D8) sobre el orden de ventas, contra Postgres: tres ventas con el
    // mismo instante de conversión, leídas de a una hasta que no queda nada. Si el corte no mirara
    // el número, o lo comparara con <= en vez de <, el lote siguiente saltaría las otras dos o
    // repetiría la misma.
    [Fact]
    public async Task SalesThatShareTheConversionInstantComeOutOnceEachByNumber()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        for (var converted = 0; converted < 3; converted++)
        {
            await CreateSaleAsync(client, factory, tenantId);
        }

        await TieConversionInstantAsync(factory, tenantId, DateTimeOffset.UtcNow.AddHours(-1));
        var expected = (await ReadPendingBatchAsync(factory, tenantId, after: null, limit: 100))
            .Select(row => row.Sale.SaleNumber)
            .OrderDescending(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(3, expected.Length);

        var read = new List<string>();
        SaleExportCursor? after = null;
        // Con tope: un corte con <= devolvería la misma fila para siempre.
        for (var batch = 0; batch < 10; batch++)
        {
            var rows = await ReadPendingBatchAsync(factory, tenantId, after, limit: 1);
            if (rows.Count == 0)
            {
                break;
            }

            read.Add(rows.Single().Sale.SaleNumber);
            after = CursorOf(rows);
        }

        Assert.Equal(expected, read);
    }

    // Las tres del tenant al mismo instante, directo en la base: la conversión pone la fecha sola.
    private static async Task TieConversionInstantAsync(QepApiFactory factory, Guid tenantId, DateTimeOffset convertedAt)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<QuotationsDbContext>();
        var updated = await dbContext.Sales
            .Where(sale => sale.TenantId == tenantId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(sale => sale.ConvertedAt, convertedAt),
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

    /// <summary>Una venta convertida hoy, sin comprobantes (pago pendiente), mismo camino que
    /// SaleListApiTests.ConvertToSaleAsync.</summary>
    private static async Task<SaleResponse> CreateSaleAsync(HttpClient client, QepApiFactory factory, Guid tenantId)
    {
        var customerId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, customerId, productId);
        var response = await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/sale",
            new ConvertQuotationToSaleRequest("PaymentPending", null, []),
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var sale = await response.Content.ReadFromJsonAsync<SaleResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(sale);
        return sale;
    }

    private sealed record AcceptedDto(Guid JobId, DateTimeOffset RequestedAt);

    private sealed record ProblemDto(string? Code, Dictionary<string, string[]>? Errors);
}
