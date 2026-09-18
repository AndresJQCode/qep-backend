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

/// <summary>
/// La exportación de cotizaciones por correo (spec 2026-09-12): el POST valida y encola en
/// milisegundos, y el worker arma el Excel después.
/// </summary>
public sealed class QuotationExportApiTests
{
    // También prueba que `/export` no lo captura `/{quotationId:guid}`.
    [Fact]
    public async Task ExportIsAcceptedAndLeavesAPendingJobForWhoAskedForIt()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, ownerUserId, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var customerId = await CreateActiveCustomerAsync(client, tenantId);
        await CreateQuotationAsync(client, tenantId, customerId);

        var response = await client.PostAsync(
            $"{QuotationsUrl(tenantId)}/export?{CurrentRange()}", content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = await response.Content.ReadFromJsonAsync<AcceptedDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(accepted);
        var job = await FindExportJobAsync(factory, accepted.JobId);
        Assert.Equal(ExportJobStatus.Pending, job.Status);
        Assert.Equal(ExportJobKind.Quotations, job.Kind);
        Assert.Equal(tenantId, job.TenantId);
        Assert.Equal(ownerUserId, job.RequestedBy);
        Assert.Equal(job.RequestedAt, accepted.RequestedAt, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task ExportWithoutDatesIsUnprocessableWithTheFieldErrors()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;

        var response = await client.PostAsync(
            $"{QuotationsUrl(tenantId)}/export", content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDto>(TestContext.Current.CancellationToken);
        Assert.Equal("validation.failed", problem?.Code);
        Assert.NotNull(problem?.Errors);
        Assert.Contains("CreatedFrom", problem.Errors.Keys);
        Assert.Contains("CreatedTo", problem.Errors.Keys);
    }

    [Fact]
    public async Task ExportWithARangeLongerThanOneYearIsUnprocessable()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;

        var response = await client.PostAsync(
            $"{QuotationsUrl(tenantId)}/export?createdFrom=2025-01-01&createdTo=2026-01-02",
            content: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDto>(TestContext.Current.CancellationToken);
        Assert.Equal("validation.failed", problem?.Code);
        Assert.Contains("CreatedTo", problem!.Errors!.Keys);
    }

    [Fact]
    public async Task ExportWithNoMatchingRowsIsUnprocessableAndEnqueuesNothing()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;

        var response = await client.PostAsync(
            $"{QuotationsUrl(tenantId)}/export?{CurrentRange()}", content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDto>(TestContext.Current.CancellationToken);
        Assert.Equal("quotation.export.empty", problem?.Code);
        Assert.Equal(0, await CountExportJobsAsync(factory));
    }

    [Fact]
    public async Task AFourthPendingExportIsUnprocessable()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var customerId = await CreateActiveCustomerAsync(client, tenantId);
        await CreateQuotationAsync(client, tenantId, customerId);
        var url = $"{QuotationsUrl(tenantId)}/export?{CurrentRange()}";

        for (var accepted = 0; accepted < 3; accepted++)
        {
            var ok = await client.PostAsync(url, content: null, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Accepted, ok.StatusCode);
        }

        var response = await client.PostAsync(url, content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDto>(TestContext.Current.CancellationToken);
        Assert.Equal("quotation.export.pending_limit", problem?.Code);
        Assert.Equal(3, await CountExportJobsAsync(factory));
    }

    // `QuotationManage` sin `QuotationRead` no alcanza: exportar es leer el listado en otro formato.
    [Fact]
    public async Task ExportWithoutTheReadPermissionIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, QuotationsPermissions.QuotationManage);
        using var _ = client;

        var response = await client.PostAsync(
            $"{QuotationsUrl(tenantId)}/export?{CurrentRange()}", content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // El permiso para su tenant no le abre el de otro: eso lo frena el handler, no la política.
    [Fact]
    public async Task ExportForAnotherTenantIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, owner) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = owner;
        var (_, _, otherOwner) = await RegisterTenantAsync(factory, QuotationsPermissions.QuotationRead);
        using var __ = otherOwner;

        var response = await otherOwner.PostAsync(
            $"{QuotationsUrl(tenantId)}/export?{CurrentRange()}", content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // El GET síncrono de 572200c ya no existe: armaba el Excel dentro del request.
    [Fact]
    public async Task TheSynchronousGetExportIsGone()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;

        var response = await client.GetAsync(
            $"{QuotationsUrl(tenantId)}/export?{CurrentRange()}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    // De punta a punta (D1): el POST encola, un tick del worker arma el Excel con las mismas filas
    // que la tabla, lo sube, deja el evento y Notifications manda el correo.
    [Fact]
    public async Task TheWorkerTurnsTheRequestIntoTheWorkbookTheEventAndTheEmail()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, ownerUserId, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientA = await CreateActiveCustomerAsync(client, tenantId);
        var clientB = await CreateActiveCustomerAsync(client, tenantId);
        await CreateQuotationAsync(client, tenantId, clientA);
        await CreateQuotationAsync(client, tenantId, clientA);
        var otherClient = await CreateQuotationAsync(client, tenantId, clientB);
        var outOfRange = await CreateQuotationAsync(client, tenantId, clientA);
        await BackdateAsync(factory, outOfRange.Id, DateTimeOffset.UtcNow.AddYears(-2));
        var filters = $"clientId={clientA}&{CurrentRange()}";

        var response = await client.PostAsync(
            $"{QuotationsUrl(tenantId)}/export?{filters}", content: null, TestContext.Current.CancellationToken);
        var accepted = await response.Content.ReadFromJsonAsync<AcceptedDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(accepted);

        Assert.Equal(ExportJobRunOutcome.Completed, await RunExportJobAsync(factory));

        var job = await FindExportJobAsync(factory, accepted.JobId);
        Assert.Equal(ExportJobStatus.Completed, job.Status);
        Assert.Equal(2, job.RowCount);
        Assert.Matches(@"^cotizaciones-\d{4}-\d{2}-\d{2}-\d{4}\.xlsx$", job.FileName);

        var key = $"exports/tenants/{tenantId:N}/jobs/{accepted.JobId:N}.xlsx";
        var ready = Assert.Single(await OutboxMessagesAsync(factory, "quotations.export-ready.v1"));
        using (var payload = JsonDocument.Parse(ready.PayloadJson))
        {
            Assert.Equal($"https://r2.test/{key}", payload.RootElement.GetProperty("downloadUrl").GetString());
            Assert.Equal(job.FileName, payload.RootElement.GetProperty("fileName").GetString());
            Assert.Equal(2, payload.RootElement.GetProperty("rowCount").GetInt32());
        }

        var sheet = ExportWorkbookReader.Read(
            await factory.ObjectStorage.DownloadAsync(key, TestContext.Current.CancellationToken));
        var list = await client.GetFromJsonAsync<QuotationsPageResponse>(
            $"{QuotationsUrl(tenantId)}?{filters}", TestContext.Current.CancellationToken);
        var items = list!.Items.ToArray();
        Assert.Equal("Cotizaciones", sheet.Name);
        Assert.Equal(["Numero", "Fecha", "Cliente", "Asesor", "Estado", "Moneda", "Total"], sheet.Rows[0]);
        Assert.Equal(
            items.Select(item => item.QuotationNumber),
            sheet.Rows.Skip(1).Select(row => row[0]));
        Assert.DoesNotContain(sheet.Rows, row => row[0] == otherClient.QuotationNumber);
        Assert.DoesNotContain(sheet.Rows, row => row[0] == outOfRange.QuotationNumber);
        var first = sheet.Rows[1];
        // La fecha sale en la hora del tenant, al minuto y sin offset (spec 2026-09-17, punto 8a).
        Assert.Equal(LocalMinuteInBogota(items[0].CreatedAt), first[1]);
        Assert.Equal("Verde Esencial S.A.S.", first[2]);
        Assert.Equal(items[0].AdvisorName ?? string.Empty, first[3]);
        Assert.Equal("Borrador", first[4]);
        Assert.Equal(items[0].Currency, first[5]);
        Assert.True(sheet.NumericCells[1][6]);
        Assert.Equal(items[0].Total, decimal.Parse(first[6], CultureInfo.InvariantCulture));

        Assert.Equal("Sent", await WaitForEmailStatusAsync(
            database.GetConnectionString(), ownerUserId, "quotations.export-ready.v1"));
    }

    // Mueve la fecha de alta directo en la base: la API no deja crear una cotización en el pasado.
    private static async Task BackdateAsync(QepApiFactory factory, Guid quotationId, DateTimeOffset createdAt)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<QuotationsDbContext>();
        var id = new QuotationId(quotationId);
        var updated = await dbContext.Quotations
            .Where(quotation => quotation.Id == id)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(quotation => quotation.CreatedAt, createdAt),
                TestContext.Current.CancellationToken);
        Assert.Equal(1, updated);
    }

    // Keyset y no offset (D8, hallazgo 11). Se lee de a una fila para tener un borde por
    // cotización, y entre lotes pasan los dos cambios que rompen el offset: una cotización nueva
    // (con offset, el lote siguiente repetiría la del borde) y una ya leída que sale del filtro
    // (con offset, el lote siguiente saltaría una). Lo esperado son las que existían al empezar,
    // en el orden del export, una vez cada una.
    [Fact]
    public async Task RowsCreatedOrLeavingTheFilterBetweenBatchesNeitherRepeatNorSkip()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var customerId = await CreateActiveCustomerAsync(client, tenantId);
        for (var created = 0; created < 3; created++)
        {
            await CreateQuotationAsync(client, tenantId, customerId);
        }

        var expected = (await ReadDraftBatchAsync(factory, tenantId, after: null, limit: 100))
            .Select(quotation => quotation.Id)
            .ToArray();
        Assert.Equal(3, expected.Length);

        var first = await ReadDraftBatchAsync(factory, tenantId, after: null, limit: 1);
        await CreateQuotationAsync(client, tenantId, customerId);
        var second = await ReadDraftBatchAsync(factory, tenantId, CursorOf(first), limit: 1);
        await SetQuotationStatusAsync(factory, second.Single().Id, QuotationStatus.Voided);
        var third = await ReadDraftBatchAsync(factory, tenantId, CursorOf(second), limit: 1);
        var fourth = await ReadDraftBatchAsync(factory, tenantId, CursorOf(third), limit: 1);

        Assert.Equal(expected, first.Concat(second).Concat(third).Select(quotation => quotation.Id));
        Assert.Empty(fourth);
    }

    // El repositorio real, contra Postgres: lo que se prueba es la consulta del keyset, no el
    // procesador. `Draft` es el filtro que la cotización anulada abandona.
    private static async Task<IReadOnlyList<Quotation>> ReadDraftBatchAsync(
        QepApiFactory factory, Guid tenantId, QuotationExportCursor? after, int limit)
    {
        // El repositorio recibe instantes desde la spec 2026-09-17 (punto 3): la ventana es amplia a
        // propósito, lo que se prueba es el keyset.
        var now = DateTimeOffset.UtcNow;
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IQuotationRepository>().ListForExportAsync(
            tenantId,
            clientId: null,
            clientIds: null,
            advisorId: null,
            QuotationStatus.Draft,
            now.AddDays(-7),
            now.AddDays(2),
            quotationNumber: null,
            after,
            limit,
            TestContext.Current.CancellationToken);
    }

    private static QuotationExportCursor CursorOf(IReadOnlyList<Quotation> batch) =>
        new(batch[^1].CreatedAt, batch[^1].QuotationNumber);

    // Directo en la base, como BackdateAsync: lo que se prueba es la lectura, no la anulación.
    private static async Task SetQuotationStatusAsync(
        QepApiFactory factory, QuotationId quotationId, QuotationStatus status)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<QuotationsDbContext>();
        var updated = await dbContext.Quotations
            .Where(quotation => quotation.Id == quotationId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(quotation => quotation.Status, status),
                TestContext.Current.CancellationToken);
        Assert.Equal(1, updated);
    }

    // El desempate del keyset (D8), contra Postgres: tres cotizaciones con el mismo instante de
    // alta, leídas de a una hasta que no queda nada. Si el corte no mirara el número, o lo
    // comparara con <= en vez de <, el lote siguiente saltaría las otras dos o repetiría la misma.
    [Fact]
    public async Task QuotationsThatShareTheCreationInstantComeOutOnceEachByNumber()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var customerId = await CreateActiveCustomerAsync(client, tenantId);
        for (var created = 0; created < 3; created++)
        {
            await CreateQuotationAsync(client, tenantId, customerId);
        }

        await TieCreationInstantAsync(factory, tenantId, DateTimeOffset.UtcNow.AddHours(-1));
        var expected = (await ReadDraftBatchAsync(factory, tenantId, after: null, limit: 100))
            .Select(quotation => quotation.QuotationNumber)
            .OrderDescending(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(3, expected.Length);

        var read = new List<string>();
        QuotationExportCursor? after = null;
        // Con tope: un corte con <= devolvería la misma fila para siempre.
        for (var batch = 0; batch < 10; batch++)
        {
            var rows = await ReadDraftBatchAsync(factory, tenantId, after, limit: 1);
            if (rows.Count == 0)
            {
                break;
            }

            read.Add(rows.Single().QuotationNumber);
            after = CursorOf(rows);
        }

        Assert.Equal(expected, read);
    }

    // Las tres del tenant al mismo instante, directo en la base: la API pone la fecha de alta sola.
    private static async Task TieCreationInstantAsync(QepApiFactory factory, Guid tenantId, DateTimeOffset createdAt)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<QuotationsDbContext>();
        var updated = await dbContext.Quotations
            .Where(quotation => quotation.TenantId == tenantId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(quotation => quotation.CreatedAt, createdAt),
                TestContext.Current.CancellationToken);
        Assert.Equal(3, updated);
    }

    private static string CurrentRange()
    {
        var today = TodayInBogota();
        return $"createdFrom={Iso(today.AddDays(-7))}&createdTo={Iso(today.AddDays(1))}";
    }

    private static string Iso(DateOnly date) =>
        date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static async Task<int> CountExportJobsAsync(QepApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<QuotationsDbContext>()
            .ExportJobs.CountAsync(TestContext.Current.CancellationToken);
    }

    private sealed record AcceptedDto(Guid JobId, DateTimeOffset RequestedAt);

    private sealed record ProblemDto(string? Code, Dictionary<string, string[]>? Errors);
}
