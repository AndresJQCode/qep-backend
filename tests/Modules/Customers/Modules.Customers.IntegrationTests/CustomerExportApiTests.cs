using System.Net;
using System.Net.Http.Json;
using ClosedXML.Excel;
using Microsoft.Extensions.DependencyInjection;
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

        // Las diez columnas de la importacion van primero y en su orden exacto, para que el
        // archivo exportado se pueda volver a importar sin editarlo.
        for (var column = 0; column < CustomerImportColumns.Ordered.Count; column++)
        {
            Assert.Equal(
                CustomerImportColumns.Ordered[column],
                sheet.Cell(1, column + 1).GetString());
        }

        var names = new[] { sheet.Cell(2, 1).GetString(), sheet.Cell(3, 1).GetString() };
        Assert.Contains("Verde Esencial S.A.S.", names);
        Assert.Contains("Azul Profundo Ltda.", names);
        Assert.Equal(classification.Name, sheet.Cell(2, 9).GetString());
        Assert.Equal(city.CityName, sheet.Cell(2, 8).GetString());

        // El correo no se manda en el request: queda encolado como evento de integracion.
        var events = await OutboxEventNamesAsync(database.GetConnectionString());
        Assert.Contains("customers.export-ready.v1", events);
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
