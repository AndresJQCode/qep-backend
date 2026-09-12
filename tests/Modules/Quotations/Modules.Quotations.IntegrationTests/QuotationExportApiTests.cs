using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Quotations.Infrastructure.Persistence;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// El listado de cotizaciones en un <c>.xlsx</c>: los mismos filtros que la pantalla, sin
/// paginar, con un rango de fechas obligatorio de a lo sumo un año.
/// </summary>
public sealed class QuotationExportApiTests
{
    private const string ExcelContentType =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    // Tambien prueba que `/export` no lo captura `/{quotationId:guid}`: si lo hiciera, esto
    // seria un 404 y no un archivo.
    [Fact]
    public async Task ExportReturnsTheSameRowsAsTheListForTheSameFilters()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientA = await CreateActiveCustomerAsync(client, tenantId);
        var clientB = await CreateActiveCustomerAsync(client, tenantId);
        var inRange = await CreateQuotationAsync(client, tenantId, clientA);
        var otherClient = await CreateQuotationAsync(client, tenantId, clientB);
        var outOfRange = await CreateQuotationAsync(client, tenantId, clientA);
        await BackdateAsync(factory, outOfRange.Id, DateTimeOffset.UtcNow.AddYears(-2));

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var filters =
            $"clientId={clientA}&createdFrom={Iso(today.AddDays(-7))}&createdTo={Iso(today.AddDays(1))}";
        var response = await client.GetAsync(
            $"{QuotationsUrl(tenantId)}/export?{filters}", TestContext.Current.CancellationToken);
        var list = await client.GetFromJsonAsync<QuotationsPageResponse>(
            $"{QuotationsUrl(tenantId)}?{filters}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ExcelContentType, response.Content.Headers.ContentType?.MediaType);
        var fileName = response.Content.Headers.ContentDisposition?.FileNameStar
            ?? response.Content.Headers.ContentDisposition?.FileName
            ?? string.Empty;
        Assert.StartsWith("cotizaciones-", fileName, StringComparison.Ordinal);
        Assert.EndsWith(".xlsx", fileName, StringComparison.Ordinal);

        using var workbook = new XLWorkbook(new MemoryStream(
            await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)));
        var sheet = workbook.Worksheet("Cotizaciones");
        Assert.Equal(
            ["Numero", "Fecha", "Cliente", "Asesor", "Estado", "Moneda", "Total"],
            sheet.Row(1).CellsUsed().Select(cell => cell.GetString()).ToArray());

        var row = Assert.Single(sheet.RowsUsed().Skip(1));
        var item = Assert.Single(list!.Items);
        Assert.Equal(inRange.Id, item.Id);
        Assert.Equal(item.QuotationNumber, row.Cell(1).GetString());
        Assert.Equal(
            item.CreatedAt,
            DateTimeOffset.Parse(row.Cell(2).GetString(), CultureInfo.InvariantCulture));
        Assert.Equal("Verde Esencial S.A.S.", row.Cell(3).GetString());
        Assert.Equal(item.AdvisorEmail ?? string.Empty, row.Cell(4).GetString());
        Assert.Equal("Draft", row.Cell(5).GetString());
        Assert.Equal(item.Currency, row.Cell(6).GetString());
        Assert.Equal(XLDataType.Number, row.Cell(7).DataType);
        Assert.Equal(item.Total, row.Cell(7).GetValue<decimal>());
        Assert.NotEqual(otherClient.QuotationNumber, row.Cell(1).GetString());
        Assert.NotEqual(outOfRange.QuotationNumber, row.Cell(1).GetString());
    }

    [Fact]
    public async Task ExportWithoutDatesIsUnprocessableWithTheFieldErrors()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;

        var response = await client.GetAsync(
            $"{QuotationsUrl(tenantId)}/export", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDto>(
            TestContext.Current.CancellationToken);
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

        var response = await client.GetAsync(
            $"{QuotationsUrl(tenantId)}/export?createdFrom=2025-01-01&createdTo=2026-01-02",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDto>(
            TestContext.Current.CancellationToken);
        Assert.Equal("validation.failed", problem?.Code);
        Assert.NotNull(problem?.Errors);
        Assert.Contains("CreatedTo", problem.Errors.Keys);
    }

    [Fact]
    public async Task ExportWithNoMatchingRowsIsUnprocessable()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var response = await client.GetAsync(
            $"{QuotationsUrl(tenantId)}/export?createdFrom={Iso(today.AddDays(-7))}&createdTo={Iso(today)}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDto>(
            TestContext.Current.CancellationToken);
        Assert.Equal("quotation.export.empty", problem?.Code);
    }

    // `QuotationManage` sin `QuotationRead` no alcanza: exportar es leer el listado en otro
    // formato, y lo frena la politica del endpoint.
    [Fact]
    public async Task ExportWithoutTheReadPermissionIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(
            factory, QuotationsPermissions.QuotationManage);
        using var _ = client;

        var response = await client.GetAsync(
            $"{QuotationsUrl(tenantId)}/export?createdFrom=2026-01-01&createdTo=2026-01-31",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // El permiso para su propio tenant no le abre el de otro: eso lo frena el handler, no la
    // politica.
    [Fact]
    public async Task ExportForAnotherTenantIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, owner) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = owner;
        var (_, _, otherOwner) = await RegisterTenantAsync(
            factory, QuotationsPermissions.QuotationRead);
        using var __ = otherOwner;

        var response = await otherOwner.GetAsync(
            $"{QuotationsUrl(tenantId)}/export?createdFrom=2026-01-01&createdTo=2026-01-31",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static string Iso(DateOnly date) =>
        date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // Mueve la fecha de alta directo en la base: la API no deja crear una cotizacion en el
    // pasado, y el reloj del host no se puede correr por prueba.
    private static async Task BackdateAsync(
        QepApiFactory factory, Guid quotationId, DateTimeOffset createdAt)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<QuotationsDbContext>();
        var id = new QuotationId(quotationId);
        var updated = await dbContext.Quotations
            .Where(quotation => quotation.Id == id)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(quotation => quotation.CreatedAt, createdAt),
                TestContext.Current.CancellationToken);
        Assert.Equal(1, updated);
    }

    private sealed record ProblemDto(string? Code, Dictionary<string, string[]>? Errors);
}
