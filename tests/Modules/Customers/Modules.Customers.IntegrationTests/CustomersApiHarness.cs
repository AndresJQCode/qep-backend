using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Modules.Customers.Application;
using Testcontainers.PostgreSql;

namespace Modules.Customers.IntegrationTests;

/// <summary>
/// El arranque compartido de las pruebas de integracion del modulo.
///
/// Vive una sola vez, como en companies: es el mismo arranque en todos los archivos de prueba, y
/// con una copia por archivo basta con que alguien ajuste una para que las demas prueben contra
/// otra configuracion sin que nada avise.
/// </summary>
internal static class CustomersApiHarness
{
    public const string TenantId = "01900000-0000-7000-8000-000000000001";
    public const string SubjectId = "01900000-0000-7000-8000-000000000002";
    public const string OtherTenantId = "01900000-0000-7000-8000-0000000000ff";
    public const string OtherSubjectId = "01900000-0000-7000-8000-0000000000fe";

    public static string CustomersUrl(string tenantId = TenantId) =>
        $"/api/v1/tenants/{tenantId}/customers";

    public static string ClassificationsUrl(string tenantId = TenantId) =>
        $"{CustomersUrl(tenantId)}/classifications";

    public static async Task<PostgreSqlContainer> StartDatabaseAsync()
    {
        var database = new PostgreSqlBuilder("postgres:18-alpine")
            .WithDatabase("qep")
            .WithUsername("qep")
            .WithPassword("qep-integration")
            .Build();
        await database.StartAsync(TestContext.Current.CancellationToken);
        return database;
    }

    // El stub de desarrollo concede solo los defaults de tenancy cuando X-Permissions no esta
    // (DevelopmentAuthenticationHandler.ResolvePermissions), asi que un permiso de customers hay
    // que pedirlo explicitamente. Pasarlo por prueba mantiene cada 403 atribuible: sin esto, una
    // prueba cross-tenant pasaria simplemente porque el llamador no tenia ningun permiso del
    // modulo, y seguiria pasando aunque se rompiera el aislamiento de tenant.
    public static HttpClient CreateClient(
        QepApiFactory factory,
        string subjectId,
        string tenantId,
        params string[] permissions)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Subject-Id", subjectId);
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenantId);
        if (permissions.Length > 0)
        {
            client.DefaultRequestHeaders.Add("X-Permissions", string.Join(',', permissions));
        }

        return client;
    }

    public static HttpClient CreateManager(QepApiFactory factory) =>
        CreateClient(
            factory,
            SubjectId,
            TenantId,
            CustomersPermissions.CustomerRead,
            CustomersPermissions.CustomerManage,
            CustomersPermissions.ClassificationRead,
            CustomersPermissions.ClassificationManage);

    public static HttpClient CreateImporter(QepApiFactory factory) =>
        CreateClient(
            factory,
            SubjectId,
            TenantId,
            CustomersPermissions.CustomerRead,
            CustomersPermissions.CustomerImport);

    /// <summary>
    /// Una ciudad real (Geography no tiene tenant: los datos son los que siembra
    /// <c>GeographySeeder</c> en cada arranque) y el codigo DIVIPOLA de su departamento, que es la
    /// mitad del CUC que el backend va a emitir. No se hardcodea ningun id: los dos endpoints de
    /// Geography ya existen y son la unica fuente confiable de ids reales en esta base de prueba.
    /// </summary>
    // CityName y DepartmentName al final, agregados para la importacion masiva (Fase 5): el Excel
    // resuelve por nombre, no por id, asi que las pruebas de import necesitan el texto exacto que
    // hay que escribir en las celdas de Departamento y Ciudad.
    public sealed record CityFixture(
        Guid CityId, string DepartmentDivipolaCode, string CityName, string DepartmentName);

    public static async Task<CityFixture> EnsureCityAsync(HttpClient client) =>
        (await EnsureCitiesAsync(client, 1))[0];

    /// <summary>
    /// La version de <see cref="EnsureCityAsync"/> que devuelve varios departamentos **distintos**,
    /// cada uno con al menos una ciudad. La usa la importacion masiva para probar "una ciudad que
    /// no pertenece al departamento indicado": el nombre de la ciudad del segundo fixture existe en
    /// la base, pero no bajo el departamento del primero.
    /// </summary>
    public static async Task<IReadOnlyList<CityFixture>> EnsureCitiesAsync(HttpClient client, int count)
    {
        var departments = await client.GetFromJsonAsync<List<GeographyDepartmentDto>>(
            "/api/v1/departments", TestContext.Current.CancellationToken);
        Assert.NotNull(departments);
        Assert.NotEmpty(departments);

        var fixtures = new List<CityFixture>();

        // No todos los departamentos vienen con la misma certeza de tener municipios en el JSON
        // de prueba (San Andres, por ejemplo, es chico); se recorre hasta encontrar los que
        // necesita el llamador en vez de asumir que los primeros los tienen.
        foreach (var department in departments)
        {
            if (fixtures.Count >= count)
            {
                break;
            }

            var cities = await client.GetFromJsonAsync<List<GeographyCityDto>>(
                $"/api/v1/cities?departmentId={department.Id}",
                TestContext.Current.CancellationToken);
            if (cities is { Count: > 0 })
            {
                fixtures.Add(new CityFixture(
                    cities[0].Id, department.DivipolaCode, cities[0].Name, department.Name));
            }
        }

        if (fixtures.Count < count)
        {
            throw new InvalidOperationException(
                $"Only {fixtures.Count} department(s) in the seeded DIVIPOLA data have at least " +
                $"one city; {count} were requested.");
        }

        return fixtures;
    }

    private sealed record GeographyDepartmentDto(Guid Id, string DivipolaCode, string Name);

    private sealed record GeographyCityDto(Guid Id, string DivipolaCode, string Name, Guid DepartmentId);

    /// <summary>
    /// Una fila del Excel de importacion, para armar workbooks de prueba sin repetir las diez
    /// columnas en cada test. Todos los campos son opcionales — un test que quiere una fila
    /// invalida por una celda vacia simplemente no la pasa.
    /// </summary>
    public sealed record ExcelRowInput(
        string? Name = "Verde Esencial S.A.S.",
        string? IdentificationType = "NIT",
        string? IdentificationNumber = "900.123.456-1",
        string? Phone = null,
        string? Email = null,
        string? Address = null,
        string? Department = null,
        string? City = null,
        string? Classification = null,
        string? WithRetention = null);

    /// <summary>
    /// Arma un <c>.xlsx</c> real en memoria con ClosedXML — las pruebas de import ya no suben
    /// basura ASCII con extension <c>.xlsx</c>, porque el parseo ahora es real y basura no es un
    /// Excel valido.
    /// </summary>
    public static MultipartFormDataContent BuildExcelUpload(
        string fileName,
        IReadOnlyList<ExcelRowInput> rows,
        IReadOnlyList<string>? headers = null)
    {
        var columns = headers ?? CustomerImportColumns.Ordered;

        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Clientes");
        for (var column = 0; column < columns.Count; column++)
        {
            sheet.Cell(1, column + 1).Value = columns[column];
        }

        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var values = new[]
            {
                row.Name, row.IdentificationType, row.IdentificationNumber, row.Phone, row.Email,
                row.Address, row.Department, row.City, row.Classification, row.WithRetention
            };
            for (var column = 0; column < values.Length && column < columns.Count; column++)
            {
                if (values[column] is not null)
                {
                    sheet.Cell(index + 2, column + 1).Value = values[column];
                }
            }
        }

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);

        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(stream.ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue(
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        content.Add(file, "file", fileName);
        return content;
    }

    /// <summary>Da de alta una clasificacion de cliente y devuelve su id y su prefijo.</summary>
    public static async Task<ClientClassificationResponse> CreateClassificationAsync(
        HttpClient client,
        string name = "Mediano",
        string prefix = "CLI",
        string tenantId = TenantId)
    {
        var response = await client.PostAsJsonAsync(
            ClassificationsUrl(tenantId),
            new { name, prefix },
            TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        var classification = await response.Content.ReadFromJsonAsync<ClientClassificationResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(classification);
        return classification;
    }

    /// <summary>El cuerpo minimo de un alta, con lo obligatorio y nada mas.</summary>
    public static object NewCustomerBody(
        Guid cityId,
        Guid classificationId,
        string name = "Verde Esencial S.A.S.",
        string identificationType = "NIT",
        string identificationNumber = "900.123.456-1") =>
        new
        {
            name,
            identificationType,
            identificationNumber,
            cityId,
            classificationId,
            withRetention = false
        };

    /// <summary>Da de alta un cliente y devuelve la respuesta ya deserializada.</summary>
    public static async Task<CustomerResponse> CreateCustomerAsync(
        HttpClient client,
        Guid cityId,
        Guid classificationId,
        string name = "Verde Esencial S.A.S.",
        string identificationNumber = "900.123.456-1",
        string tenantId = TenantId)
    {
        var response = await client.PostAsJsonAsync(
            CustomersUrl(tenantId),
            NewCustomerBody(
                cityId,
                classificationId,
                name: name,
                identificationNumber: identificationNumber),
            TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        var customer = await response.Content.ReadFromJsonAsync<CustomerResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(customer);
        return customer;
    }

    public static async Task<CustomersResponse> ListAsync(HttpClient client, string query)
    {
        var response = await client.GetFromJsonAsync<CustomersResponse>(
            $"{CustomersUrl()}{query}",
            TestContext.Current.CancellationToken);
        Assert.NotNull(response);
        return response;
    }

    /// <summary>
    /// Los nombres de campo del mapa <c>errors</c> de un 422 de validacion. Es el contrato que el
    /// formulario consume: <c>customerFieldErrors</c> descarta cualquier 422 sin este mapa, y mapea
    /// por nombre en PascalCase.
    /// </summary>
    public static async Task<string[]> ValidationFieldsAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.TryGetProperty("errors", out var errors)
            ? errors.EnumerateObject().Select(property => property.Name).ToArray()
            : [];
    }

    public sealed class QepApiFactory(string connectionString)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:QepDatabase", connectionString);
            builder.UseSetting("OpenTelemetry:Endpoint", string.Empty);
            builder.UseSetting("Storage:R2:AccountId", "test-account");
            builder.UseSetting("Storage:R2:AccessKeyId", "test-access-key");
            builder.UseSetting("Storage:R2:SecretAccessKey", "test-secret");
            builder.UseSetting("Storage:R2:Bucket", "test-bucket");
            // Fijado, nunca heredado de appsettings.json: con "infobip" y las claves de Infobip
            // ausentes, NotificationsOptionsValidator falla al arrancar y todas las pruebas de
            // este proyecto mueren antes de llegar a su asercion. SDD-CT-17.
            builder.UseSetting("Notifications:EmailProvider", "log");
        }
    }
}
