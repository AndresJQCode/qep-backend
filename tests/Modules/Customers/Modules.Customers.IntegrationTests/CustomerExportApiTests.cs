using System.Net;
using System.Net.Http.Json;
using BuildingBlocks.Application;
using ClosedXML.Excel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Customers.Application;
using Npgsql;
using static Modules.Customers.IntegrationTests.CustomersApiHarness;

namespace Modules.Customers.IntegrationTests;

/// <summary>
/// La exportacion del padron de clientes: genera el Excel, lo sube a la carpeta temporal de R2 y
/// encola el correo con el enlace prefirmado.
///
/// El puerto de subida se reemplaza por un doble que captura los bytes — no hay bucket en las
/// pruebas, y ademas es la unica forma de abrir el workbook generado. La convencion del modulo es
/// re-abrir el <c>.xlsx</c> con ClosedXML y asertar celdas: verificar solo el status HTTP dejaria
/// pasar un archivo vacio o con las columnas corridas.
/// </summary>
public sealed class CustomerExportApiTests
{
    private static string ExportUrl(string tenantId = TenantId) =>
        $"{CustomersUrl(tenantId)}/export";

    [Fact]
    public async Task ExportBuildsTheWorkbookUploadsItAndQueuesTheEmail()
    {
        await using var database = await StartDatabaseAsync();
        var storage = new CapturingExportStorage();
        using var factory = Factory(database, storage);
        using var client = CreateManager(factory);
        await SeedTenantAsync(factory);

        var city = await EnsureCityAsync(client);
        var classification = await CreateClassificationAsync(client);
        await CreateCustomerAsync(
            client, city.CityId, classification.Id, "Verde Esencial S.A.S.", "900.123.456-1");
        await CreateCustomerAsync(
            client, city.CityId, classification.Id, "Azul Profundo Ltda.", "901.222.333-4");

        var response = await client.PostAsync(
            ExportUrl(), content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ExportResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        Assert.Equal(2, body.CustomerCount);
        Assert.EndsWith(".xlsx", body.FileName, StringComparison.Ordinal);

        // El archivo que se subio es un Excel real y trae los dos clientes.
        Assert.NotNull(storage.Content);
        using var workbook = new XLWorkbook(new MemoryStream(storage.Content));
        var sheet = workbook.Worksheets.First();

        // Las columnas de la importacion van primero y en su orden exacto, para que el
        // archivo exportado se pueda volver a importar sin editarlo.
        for (var column = 0; column < CustomerImportColumns.Ordered.Count; column++)
        {
            Assert.Equal(
                CustomerImportColumns.Ordered[column],
                sheet.Cell(1, column + 1).GetString());
        }

        var names = new[] { sheet.Cell(2, 2).GetString(), sheet.Cell(3, 2).GetString() };
        Assert.Contains("Verde Esencial S.A.S.", names);
        Assert.Contains("Azul Profundo Ltda.", names);
        Assert.Equal(classification.Name, sheet.Cell(2, 11).GetString());
        Assert.Equal(city.CityName, sheet.Cell(2, 10).GetString());

        // El correo no se manda en el request: queda encolado como evento de integracion.
        var events = await OutboxEventNamesAsync(database.GetConnectionString());
        Assert.Contains("customers.export-ready.v1", events);
    }

    // Spec 2026-09-17, punto 8a: con el reloj en el 31 de diciembre a las 23:00 de Bogotá (el tenant
    // de desarrollo que siembra TenancyDatabaseInitializer), el nombre del archivo y las fechas de
    // alta y de actualización salen en la hora del tenant, sin offset.
    [Fact]
    public async Task ExportWritesDatesAndTheFileNameInTheTenantsLocalTime()
    {
        await using var database = await StartDatabaseAsync();
        var storage = new CapturingExportStorage();
        using var factory = FactoryAt(database, storage, new DateTimeOffset(2027, 1, 1, 4, 0, 0, TimeSpan.Zero));
        using var client = CreateManager(factory);
        await SeedTenantAsync(factory);
        var city = await EnsureCityAsync(client);
        var classification = await CreateClassificationAsync(client);
        await CreateCustomerAsync(
            client, city.CityId, classification.Id, "Verde Esencial S.A.S.", "900.123.456-1");

        var response = await client.PostAsync(
            ExportUrl(), content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ExportResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        Assert.Equal("clientes-20261231-230000.xlsx", body.FileName);
        Assert.NotNull(storage.Content);
        using var workbook = new XLWorkbook(new MemoryStream(storage.Content));
        var sheet = workbook.Worksheets.First();
        Assert.Equal("2026-12-31 23:00", sheet.Cell(2, 15).GetString());
        Assert.Equal("2026-12-31 23:00", sheet.Cell(2, 16).GetString());
    }

    // Spec 2026-10-10 §5.2: el export incluye los incompletos, con celdas vacías, y suma la columna
    // «Estado de la ficha» al final, después de las propias del export.
    [Fact]
    public async Task TheExportIncludesIncompleteRecordsWithTheirStatusColumn()
    {
        await using var database = await StartDatabaseAsync();
        var storage = new CapturingExportStorage();
        using var factory = Factory(database, storage);
        using var client = CreateManager(factory);
        await SeedTenantAsync(factory);
        var city = await EnsureCityAsync(client);
        var classification = await CreateClassificationAsync(client);
        await CreateCustomerAsync(
            client, city.CityId, classification.Id, "Verde Esencial S.A.S.", "900.123.456-1");
        await SeedIncompleteAsync(factory);

        var response = await client.PostAsync(
            ExportUrl(), content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.NotNull(storage.Content);
        using var workbook = new XLWorkbook(new MemoryStream(storage.Content));
        var sheet = workbook.Worksheets.First();
        Assert.Equal("Estado de la ficha", sheet.Cell(1, StatusColumnIndex).GetString());
        Assert.Equal(StatusColumnIndex, sheet.Row(1).LastCellUsed()!.Address.ColumnNumber);
        var columns = HeaderColumns(sheet);
        var rows = new[] { sheet.Row(2), sheet.Row(3) };
        var incomplete = Assert.Single(rows, row => row.Cell(columns[CustomerImportColumns.Name]).GetString() == "Laura Pérez");
        var complete = Assert.Single(rows, row => row.Cell(columns[CustomerImportColumns.Name]).GetString() == "Verde Esencial S.A.S.");
        Assert.Equal("Incompleta", incomplete.Cell(StatusColumnIndex).GetString());
        Assert.Equal(string.Empty, incomplete.Cell(columns[CustomerImportColumns.Cuc]).GetString());
        Assert.Equal(string.Empty, incomplete.Cell(columns[CustomerImportColumns.IdentificationType]).GetString());
        Assert.Equal(string.Empty, incomplete.Cell(columns[CustomerImportColumns.IdentificationNumber]).GetString());
        Assert.Equal(string.Empty, incomplete.Cell(columns[CustomerImportColumns.Classification]).GetString());
        Assert.Equal("Completa", complete.Cell(StatusColumnIndex).GetString());
    }

    // Desde a054cfd la lista de la importacion crecio (CUC, razon social, excedente de IVA) y el export
    // seguia escribiendo por indices viejos: la columna «Cuc» traia el nombre. Cada cabecera tiene que
    // traer su propio dato, o el archivo no se puede volver a importar.
    [Fact]
    public async Task EveryHeaderHoldsItsOwnValue()
    {
        await using var database = await StartDatabaseAsync();
        var storage = new CapturingExportStorage();
        using var factory = Factory(database, storage);
        using var client = CreateManager(factory);
        await SeedTenantAsync(factory);
        var city = await EnsureCityAsync(client);
        var classification = await CreateClassificationAsync(client);
        var customer = await CreateCustomerAsync(
            client, city.CityId, classification.Id, "Verde Esencial S.A.S.", "900.123.456-1");

        var response = await client.PostAsync(
            ExportUrl(), content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.NotNull(storage.Content);
        using var workbook = new XLWorkbook(new MemoryStream(storage.Content));
        var sheet = workbook.Worksheets.First();
        // Sin cabeceras repetidas (antes iban «Cuc» y «CUC»): el importador ubica las columnas por nombre.
        var headers = sheet.Row(1).CellsUsed().Select(cell => cell.GetString()).ToArray();
        Assert.Equal(headers.Length, headers.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        var columns = HeaderColumns(sheet);
        var row = sheet.Row(2);
        string Value(string header) => row.Cell(columns[header]).GetString();
        Assert.Equal(customer.Cuc, Value(CustomerImportColumns.Cuc));
        Assert.Equal("Verde Esencial S.A.S.", Value(CustomerImportColumns.Name));
        Assert.Equal(string.Empty, Value(CustomerImportColumns.BusinessName));
        Assert.Equal(customer.IdentificationType, Value(CustomerImportColumns.IdentificationType));
        Assert.Equal(customer.IdentificationNumber, Value(CustomerImportColumns.IdentificationNumber));
        Assert.Equal(customer.Phone, Value(CustomerImportColumns.Phone));
        Assert.Equal(customer.Email, Value(CustomerImportColumns.Email));
        Assert.Equal(customer.Address, Value(CustomerImportColumns.Address));
        Assert.Equal(customer.Department!.Name, Value(CustomerImportColumns.Department));
        Assert.Equal(city.CityName, Value(CustomerImportColumns.City));
        Assert.Equal(classification.Name, Value(CustomerImportColumns.Classification));
        Assert.Equal("No", Value(CustomerImportColumns.WithRetention));
        Assert.Equal("No", Value(CustomerImportColumns.VatSurplus));
        Assert.Equal("Si", Value("Activo"));
        Assert.Equal("Completa", Value("Estado de la ficha"));
    }

    // 13 de la importacion + Activo, Creado, Actualizado + «Estado de la ficha».
    private const int StatusColumnIndex = 17;

    // Por nombre, como lo hace el importador. Con cabeceras repetidas gana la primera; la prueba de
    // alineacion comprueba aparte que no las haya.
    private static Dictionary<string, int> HeaderColumns(IXLWorksheet sheet)
    {
        var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var cell in sheet.Row(1).CellsUsed())
        {
            columns.TryAdd(cell.GetString(), cell.Address.ColumnNumber);
        }

        return columns;
    }

    private static async Task<Guid> SeedIncompleteAsync(QepApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var directory = scope.ServiceProvider.GetRequiredService<ICustomerWhatsAppDirectory>();
        return (await directory.EnsureAsync(
            Guid.Parse(TenantId),
            new WhatsAppContact("CO.1349120865530274", "+573001234567", "Laura Pérez", null),
            TestContext.Current.CancellationToken)).CustomerId;
    }

    [Fact]
    public async Task ExportWithoutReadPermissionIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = Factory(database, new CapturingExportStorage());
        using var client = CreateClient(
            factory, SubjectId, TenantId, CustomersPermissions.CustomerManage);

        var response = await client.PostAsync(
            ExportUrl(), content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // Un tenant sin clientes no produce un Excel de una sola fila de cabeceras ni un correo con un
    // archivo vacio: falla legible, mismo criterio que el export de filas fallidas.
    [Fact]
    public async Task ExportWithoutCustomersIsUnprocessable()
    {
        await using var database = await StartDatabaseAsync();
        var storage = new CapturingExportStorage();
        using var factory = Factory(database, storage);
        using var client = CreateManager(factory);

        var response = await client.PostAsync(
            ExportUrl(), content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Null(storage.Content);
    }

    private static QepApiFactory Factory(
        Testcontainers.PostgreSql.PostgreSqlContainer database, CapturingExportStorage storage) =>
        new(
            database.GetConnectionString(),
            services => services.AddScoped<ICustomerExportStorage>(_ => storage));

    private static QepApiFactory FactoryAt(
        Testcontainers.PostgreSql.PostgreSqlContainer database,
        CapturingExportStorage storage,
        DateTimeOffset utcNow) =>
        new(
            database.GetConnectionString(),
            services =>
            {
                services.AddScoped<ICustomerExportStorage>(_ => storage);
                services.RemoveAll<IClock>();
                services.AddScoped<IClock>(_ => new FixedClock(utcNow));
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

    private sealed record ExportResponse(string FileName, int CustomerCount, DateTimeOffset ExpiresAt);

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    // Doble a mano, como el resto del repositorio: no hay libreria de mocking.
    private sealed class CapturingExportStorage : ICustomerExportStorage
    {
        public byte[]? Content { get; private set; }

        public Task<CustomerExportUpload> UploadAsync(
            Guid tenantId,
            string fileName,
            byte[] content,
            CancellationToken cancellationToken)
        {
            Content = content;
            return Task.FromResult(new CustomerExportUpload(
                $"https://r2.invalid/exports/{fileName}", DateTimeOffset.UtcNow.AddHours(24)));
        }
    }
}
