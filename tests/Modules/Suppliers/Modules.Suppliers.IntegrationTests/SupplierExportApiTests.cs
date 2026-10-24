using System.Net;
using System.Net.Http.Json;
using ClosedXML.Excel;
using Microsoft.Extensions.DependencyInjection;
using Modules.Suppliers.Application;
using Npgsql;
using static Modules.Suppliers.IntegrationTests.SuppliersApiHarness;

namespace Modules.Suppliers.IntegrationTests;

/// <summary>
/// La exportación del directorio: genera el Excel, lo deja en el almacenamiento y encola el aviso.
/// El puerto de subida se reemplaza por un doble que captura los bytes (no hay bucket en las
/// pruebas) y así se puede re-abrir el <c>.xlsx</c> y asertar celdas, no sólo el status.
/// </summary>
public sealed class SupplierExportApiTests
{
    private static string ExportUrl(string query = "") => $"{SuppliersUrl()}/export{query}";

    [Fact]
    public async Task ExportReturns202WithCountAndPublishesTheReadyEvent()
    {
        var (database, storage, factory) = await OpenWithStorageAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);
        await CreateNamedAsync(client, "Alfa", "1");
        await CreateNamedAsync(client, "Bravo", "2");
        await CreateNamedAsync(client, "Charlie", "3");

        var response = await client.PostAsync(
            ExportUrl(), content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ExportResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        Assert.Equal(3, body.SupplierCount);
        Assert.StartsWith("proveedores-", body.FileName, StringComparison.Ordinal);
        Assert.EndsWith(".xlsx", body.FileName, StringComparison.Ordinal);
        Assert.NotNull(storage.Content);
        Assert.Equal(body.FileName, storage.FileName);
        Assert.Contains(
            "suppliers.export-ready.v1", await OutboxEventNamesAsync(database.GetConnectionString()));
        Assert.Equal(1, await ExportAuditCountAsync(database.GetConnectionString()));
    }

    [Fact]
    public async Task ExportAppliesTheListFilters()
    {
        var (database, storage, factory) = await OpenWithStorageAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);
        var cityId = await EnsureCityIdAsync(client);
        await CreateSupplierAsync(client, new()
        {
            ["name"] = "Activo Ferretero",
            ["identificationNumber"] = "1"
        }, cityId: cityId);
        await CreateSupplierAsync(client, new()
        {
            ["name"] = "Activo Papelero",
            ["identificationNumber"] = "2",
            ["category"] = "Papelería"
        }, cityId: cityId);
        var inactive = await CreateSupplierAsync(client, new()
        {
            ["name"] = "Inactivo Ferretero",
            ["identificationNumber"] = "3"
        }, cityId: cityId);
        (await client.PostAsync(
            $"{SuppliersUrl()}/{inactive.Id}/deactivate", null, TestContext.Current.CancellationToken))
            .EnsureSuccessStatusCode();

        var response = await client.PostAsync(
            ExportUrl("?status=active&category=" + Uri.EscapeDataString("Ferretería")),
            content: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ExportResponse>(
            TestContext.Current.CancellationToken);
        Assert.Equal(1, body!.SupplierCount);
        using var workbook = OpenWorkbook(storage);
        var sheet = workbook.Worksheet("Proveedores");
        Assert.Equal("Activo Ferretero", sheet.Cell(2, 1).GetString());
        Assert.True(sheet.Cell(3, 1).IsEmpty());
    }

    [Fact]
    public async Task ExportWithNoRowsIs422EmptyAndPublishesNothing()
    {
        var (database, storage, factory) = await OpenWithStorageAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);
        await CreateNamedAsync(client, "Solo Activo", "1");

        var response = await client.PostAsync(
            ExportUrl("?status=inactive"), content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("suppliers.export.empty", await BodyAsync(response));
        Assert.Null(storage.Content);
        Assert.DoesNotContain(
            "suppliers.export-ready.v1", await OutboxEventNamesAsync(database.GetConnectionString()));
        Assert.Equal(0, await ExportAuditCountAsync(database.GetConnectionString()));
    }

    [Fact]
    public async Task ExportWithAnUnknownStatusIs422AndPublishesNothing()
    {
        var (database, storage, factory) = await OpenWithStorageAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);
        await CreateNamedAsync(client, "Alfa", "1");

        using var response = await client.PostAsync(
            ExportUrl("?status=paused"), content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("suppliers.supplier.status_filter_invalid", await BodyAsync(response));
        Assert.Null(storage.Content);
        Assert.DoesNotContain(
            "suppliers.export-ready.v1", await OutboxEventNamesAsync(database.GetConnectionString()));
    }

    [Fact]
    public async Task ExportByAReaderIsAllowedAndWithoutReadIs403()
    {
        var (database, storage, factory) = await OpenWithStorageAsync();
        await using var _ = database;
        using var __ = factory;
        using var manager = CreateManager(factory);
        await CreateNamedAsync(manager, "Alfa", "1");
        using var reader = CreateReader(factory);
        using var nobody = CreateClient(factory, SubjectId, TenantId, "tenancy.settings.read");

        var denied = await nobody.PostAsync(
            ExportUrl(), content: null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Null(storage.Content);

        var allowed = await reader.PostAsync(
            ExportUrl(), content: null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, allowed.StatusCode);
        Assert.NotNull(storage.Content);
    }

    [Fact]
    public async Task TheWorkbookHasTheFifteenColumnsAndOneRowPerSupplier()
    {
        var (database, storage, factory) = await OpenWithStorageAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);
        var cityId = await EnsureCityIdAsync(client);
        await CreateSupplierAsync(client, new() { ["name"] = "Alfa" }, cityId: cityId);
        await CreateSupplierAsync(client, new()
        {
            ["name"] = "Bravo",
            ["identificationNumber"] = "2",
            ["country"] = "US",
            ["cityId"] = null,
            ["cityName"] = "Miami",
            ["bankAccounts"] = Array.Empty<object>(),
            ["contacts"] = Array.Empty<object>()
        }, cityId: cityId);

        (await client.PostAsync(ExportUrl(), null, TestContext.Current.CancellationToken))
            .EnsureSuccessStatusCode();

        using var workbook = OpenWorkbook(storage);
        var sheet = workbook.Worksheet("Proveedores");
        string[] headers =
        [
            "Nombre", "Razón social", "Tipo de documento", "Documento", "Teléfono", "Correo",
            "Dirección", "País", "Departamento", "Ciudad", "Categoría", "Plazo (días)", "Cuentas",
            "Contactos", "Estado"
        ];
        Assert.Equal(headers, Enumerable.Range(1, 15).Select(c => sheet.Cell(1, c).GetString()));
        Assert.Equal(15, sheet.LastColumnUsed()!.ColumnNumber());
        Assert.Equal(3, sheet.LastRowUsed()!.RowNumber());

        Assert.Equal("Alfa", sheet.Cell(2, 1).GetString());
        Assert.Equal("NIT", sheet.Cell(2, 3).GetString());
        Assert.Equal("CO", sheet.Cell(2, 8).GetString());
        Assert.False(string.IsNullOrEmpty(sheet.Cell(2, 9).GetString()));
        Assert.False(string.IsNullOrEmpty(sheet.Cell(2, 10).GetString()));
        Assert.Equal("30", sheet.Cell(2, 12).GetString());
        Assert.Equal(
            "Bancolombia – 123-456 (COP)\nDavivienda – 789-012 (USD)",
            sheet.Cell(2, 13).GetString());
        Assert.True(sheet.Cell(2, 13).Style.Alignment.WrapText);
        Assert.Equal(
            "Marta Rojas, Ventas, 3001112233, marta@tornillo.co\nLuis Pardo",
            sheet.Cell(2, 14).GetString());
        Assert.Equal("Activo", sheet.Cell(2, 15).GetString());

        Assert.Equal("Bravo", sheet.Cell(3, 1).GetString());
        Assert.Equal("US", sheet.Cell(3, 8).GetString());
        Assert.Equal(string.Empty, sheet.Cell(3, 9).GetString());
        Assert.Equal("Miami", sheet.Cell(3, 10).GetString());
        Assert.Equal(string.Empty, sheet.Cell(3, 13).GetString());
    }

    private static XLWorkbook OpenWorkbook(CapturingExportStorage storage)
    {
        Assert.NotNull(storage.Content);
        return new XLWorkbook(new MemoryStream(storage.Content));
    }

    private static async Task<(Testcontainers.PostgreSql.PostgreSqlContainer Database,
        CapturingExportStorage Storage, QepApiFactory Factory)> OpenWithStorageAsync()
    {
        var database = await StartDatabaseAsync();
        var storage = new CapturingExportStorage();
        var factory = new QepApiFactory(
            database.GetConnectionString(),
            services => services.AddScoped<ISupplierExportStorage>(_ => storage));
        await SeedTenantAsync(factory);
        return (database, storage, factory);
    }

    private static Task<SupplierResponse> CreateNamedAsync(
        HttpClient client, string name, string number) =>
        CreateSupplierAsync(client, new()
        {
            ["name"] = name,
            ["identificationNumber"] = number
        });

    private static async Task<List<string>> OutboxEventNamesAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT event_name FROM platform.outbox_messages", connection);
        await using var reader = await command.ExecuteReaderAsync(
            TestContext.Current.CancellationToken);

        var names = new List<string>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static async Task<long> ExportAuditCountAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT count(*) FROM platform.outbox_messages
            WHERE event_name = 'platform.audit.recorded.v1'
              AND payload->>'action' = @action
              AND payload->>'outcome' LIKE 'success:%'
            """,
            connection);
        command.Parameters.AddWithValue("action", SupplierAuditActions.Exported);
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private sealed record ExportResponse(string FileName, int SupplierCount, DateTimeOffset ExpiresAt);

    // Doble a mano, como el resto del repositorio: no hay librería de mocking.
    private sealed class CapturingExportStorage : ISupplierExportStorage
    {
        public byte[]? Content { get; private set; }

        public string? FileName { get; private set; }

        public Task<SupplierExportUpload> UploadAsync(
            Guid tenantId,
            string fileName,
            byte[] content,
            CancellationToken cancellationToken)
        {
            Content = content;
            FileName = fileName;
            return Task.FromResult(new SupplierExportUpload(
                $"https://r2.invalid/exports/{fileName}", DateTimeOffset.UtcNow.AddHours(24)));
        }
    }
}
