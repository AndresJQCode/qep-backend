using System.Net;
using System.Net.Http.Json;
using System.Text;
using ClosedXML.Excel;
using Modules.Customers.Application;
using static Modules.Customers.IntegrationTests.CustomersApiHarness;

namespace Modules.Customers.IntegrationTests;

public sealed class CustomerStatusAndImportApiTests
{
    private static Task<HttpResponseMessage> DeactivateAsync(HttpClient client, Guid id) =>
        client.PostAsync(
            $"{CustomersUrl()}/{id}/deactivate",
            content: null,
            TestContext.Current.CancellationToken);

    private static Task<HttpResponseMessage> ActivateAsync(HttpClient client, Guid id) =>
        client.PostAsync(
            $"{CustomersUrl()}/{id}/activate",
            content: null,
            TestContext.Current.CancellationToken);

    [Fact]
    public async Task DeactivateReturnsTheUpdatedCustomer()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var classification = await CreateClassificationAsync(client);
        var created = await CreateCustomerAsync(client, city.CityId, classification.Id);

        var response = await DeactivateAsync(client, created.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var customer = await response.Content.ReadFromJsonAsync<CustomerResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(customer);
        Assert.False(customer.IsActive);
    }

    [Fact]
    public async Task DeactivatingTwiceIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var classification = await CreateClassificationAsync(client);
        var created = await CreateCustomerAsync(client, city.CityId, classification.Id);
        (await DeactivateAsync(client, created.Id)).EnsureSuccessStatusCode();

        var response = await DeactivateAsync(client, created.Id);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        Assert.Contains("customers.customer.already_inactive", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnInactiveCustomerCannotBeEdited()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var classification = await CreateClassificationAsync(client);
        var created = await CreateCustomerAsync(client, city.CityId, classification.Id);
        (await DeactivateAsync(client, created.Id)).EnsureSuccessStatusCode();

        var response = await client.PutAsJsonAsync(
            $"{CustomersUrl()}/{created.Id}",
            NewCustomerBody(city.CityId, classification.Id, name: "Otro nombre"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        Assert.Contains("customers.customer.inactive", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// Sin <c>/activate</c>, inactivar seria irreversible: el PUT abre con <c>EnsureActive</c> y
    /// nada devolveria <c>IsActive</c> a true. `CLI-01` no lista el verbo — existe porque es la
    /// falta que `CAT-07` tuvo que corregir en producto despues de entregarlo.
    /// </summary>
    [Fact]
    public async Task ActivateRestoresEditability()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var classification = await CreateClassificationAsync(client);
        var created = await CreateCustomerAsync(client, city.CityId, classification.Id);
        (await DeactivateAsync(client, created.Id)).EnsureSuccessStatusCode();

        var activated = await ActivateAsync(client, created.Id);
        Assert.Equal(HttpStatusCode.OK, activated.StatusCode);

        var response = await client.PutAsJsonAsync(
            $"{CustomersUrl()}/{created.Id}",
            NewCustomerBody(city.CityId, classification.Id, name: "Verde Esencial S.A."),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // Inactivar no libera el documento: IX_customers_tenant_identification es unico **sin filtro
    // parcial**. De eso depende que reactivar no tenga que revalidar unicidad; si alguien le agrega
    // un filtro parcial al indice, esta prueba avisa.
    [Fact]
    public async Task DeactivatingDoesNotFreeTheIdentification()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var classification = await CreateClassificationAsync(client);
        var created = await CreateCustomerAsync(client, city.CityId, classification.Id);
        (await DeactivateAsync(client, created.Id)).EnsureSuccessStatusCode();

        var response = await client.PostAsJsonAsync(
            CustomersUrl(),
            NewCustomerBody(city.CityId, classification.Id, name: "Otro Cliente"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    // Activar es administrar: no estrena permiso propio, pero tampoco lo alcanza el de lectura.
    [Fact]
    public async Task DeactivateWithOnlyTheReadPermissionIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var manager = CreateManager(factory);
        using var reader = CreateClient(
            factory, SubjectId, TenantId, CustomersPermissions.CustomerRead);
        var city = await EnsureCityAsync(manager);
        var classification = await CreateClassificationAsync(manager);
        var created = await CreateCustomerAsync(manager, city.CityId, classification.Id);

        var response = await DeactivateAsync(reader, created.Id);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // Basura ASCII con extension de Excel: alcanza para las pruebas que se frenan ANTES del
    // parseo real (permiso, extension) — no sirve para nada que necesite un workbook valido.
    private static MultipartFormDataContent GarbageUpload(string fileName, int sizeInBytes = 32)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(new string('x', sizeInBytes)));
        content.Add(file, "file", fileName);
        return content;
    }

    private static Task<HttpResponseMessage> PostImportAsync(
        HttpClient client, MultipartFormDataContent upload) =>
        client.PostAsync($"{CustomersUrl()}/import", upload, TestContext.Current.CancellationToken);

    /// <summary>
    /// El camino feliz de la Fase 5: un Excel real, con filas 100% validas, importa cada una,
    /// emite CUCs secuenciales (el mismo bloque reservado de una sola vez con
    /// <c>ICucGenerator.NextBatchAsync</c>) y resuelve ciudad/departamento/clasificacion por
    /// nombre.
    /// </summary>
    [Fact]
    public async Task ImportCustomersWithAllValidRowsCreatesEveryCustomer()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        // El importador solo tiene CustomerRead + CustomerImport: crear la clasificacion de
        // referencia necesita ClassificationManage, que es del manager, no del importador — los
        // dos permisos son deliberadamente distintos (CustomersPermissions.CustomerImport).
        using var manager = CreateManager(factory);
        using var client = CreateImporter(factory);
        var city = await EnsureCityAsync(client);
        var classification = await CreateClassificationAsync(manager);

        using var upload = BuildExcelUpload(
            "clientes.xlsx",
            [
                new ExcelRowInput(
                    Name: "Verde Esencial S.A.S.",
                    IdentificationType: "NIT",
                    IdentificationNumber: "900.111.111-1",
                    Department: city.DepartmentName,
                    City: city.CityName,
                    Classification: classification.Name,
                    WithRetention: "No"),
                new ExcelRowInput(
                    Name: "Azul Comercial Ltda.",
                    IdentificationType: "CC",
                    IdentificationNumber: "1020304050",
                    Department: city.DepartmentName,
                    City: city.CityName,
                    Classification: classification.Name,
                    WithRetention: "Si")
            ]);

        var response = await PostImportAsync(client, upload);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ImportCustomersResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.Equal("completed", result.Status);
        Assert.Equal(2, result.TotalRows);
        Assert.Equal(2, result.ImportedCount);
        Assert.Equal(0, result.ErrorCount);
        Assert.Empty(result.Errors);
        Assert.Equal(2, result.Imported.Count);

        // Los CUCs son secuenciales: el bloque se reserva de una vez y se asigna en el orden en
        // que las filas aparecen en el archivo.
        var expectedFirstCuc = $"{classification.Prefix}{city.DepartmentDivipolaCode}000001";
        var expectedSecondCuc = $"{classification.Prefix}{city.DepartmentDivipolaCode}000002";
        Assert.Equal(2, result.Imported[0].RowNumber);
        Assert.Equal(expectedFirstCuc, result.Imported[0].Cuc);
        Assert.Equal("created", result.Imported[0].Action);
        Assert.Equal(3, result.Imported[1].RowNumber);
        Assert.Equal(expectedSecondCuc, result.Imported[1].Cuc);
        Assert.Equal("created", result.Imported[1].Action);

        var page = await ListAsync(client, string.Empty);
        Assert.Equal(2, page.Items.Count);
        Assert.Contains(page.Items, item => item.Cuc == expectedFirstCuc && item.City.Name == city.CityName);
        Assert.Contains(page.Items, item =>
            item.Cuc == expectedSecondCuc && item.Classification.Name == classification.Name);
    }

    /// <summary>
    /// El caso central de la Fase 5: un archivo con una mezcla de filas validas e invalidas no
    /// pierde las buenas por las malas. Cubre los seis motivos de error de fila que el slice
    /// promete: campo vacio, departamento inexistente, ciudad que no pertenece al departamento
    /// indicado, clasificacion inexistente, duplicado dentro del archivo y duplicado contra la
    /// base.
    /// </summary>
    [Fact]
    public async Task ImportCustomersWithAMixOfValidAndInvalidRowsKeepsTheValidOnes()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        // El setup (crear la clasificacion y el cliente preexistente) necesita ClassificationManage
        // y CustomerManage, que el importador no tiene — solo CustomerRead + CustomerImport.
        using var manager = CreateManager(factory);
        using var client = CreateImporter(factory);
        var cities = await EnsureCitiesAsync(client, 2);
        var here = cities[0];
        var elsewhere = cities[1];
        var classification = await CreateClassificationAsync(manager);

        // Una identificacion que ya existe en la base, para el caso "duplicado contra la base".
        var existing = await CreateCustomerAsync(
            manager, here.CityId, classification.Id, identificationNumber: "900.999.999-9");

        using var upload = BuildExcelUpload(
            "clientes.xlsx",
            [
                // Fila 2: valida.
                new ExcelRowInput(
                    Name: "Cliente Valido",
                    IdentificationType: "NIT",
                    IdentificationNumber: "900.111.111-1",
                    Department: here.DepartmentName,
                    City: here.CityName,
                    Classification: classification.Name),
                // Fila 3: nombre vacio.
                new ExcelRowInput(
                    Name: null,
                    IdentificationType: "NIT",
                    IdentificationNumber: "900.222.222-2",
                    Department: here.DepartmentName,
                    City: here.CityName,
                    Classification: classification.Name),
                // Fila 4: departamento inexistente.
                new ExcelRowInput(
                    Name: "Cliente Depto Invalido",
                    IdentificationType: "NIT",
                    IdentificationNumber: "900.333.333-3",
                    Department: "Departamento Que No Existe",
                    City: here.CityName,
                    Classification: classification.Name),
                // Fila 5: la ciudad existe, pero no en el departamento indicado.
                new ExcelRowInput(
                    Name: "Cliente Ciudad Cruzada",
                    IdentificationType: "NIT",
                    IdentificationNumber: "900.444.444-4",
                    Department: here.DepartmentName,
                    City: elsewhere.CityName,
                    Classification: classification.Name),
                // Fila 6: clasificacion inexistente.
                new ExcelRowInput(
                    Name: "Cliente Clasificacion Invalida",
                    IdentificationType: "NIT",
                    IdentificationNumber: "900.555.555-5",
                    Department: here.DepartmentName,
                    City: here.CityName,
                    Classification: "Clasificacion Que No Existe"),
                // Fila 7: duplicado dentro del archivo de la fila 2.
                new ExcelRowInput(
                    Name: "Cliente Duplicado En Archivo",
                    IdentificationType: "NIT",
                    IdentificationNumber: "900.111.111-1",
                    Department: here.DepartmentName,
                    City: here.CityName,
                    Classification: classification.Name),
                // Fila 8: identificacion que ya existe en la base.
                new ExcelRowInput(
                    Name: "Cliente Ya Existe",
                    IdentificationType: "NIT",
                    IdentificationNumber: existing.IdentificationNumber,
                    Department: here.DepartmentName,
                    City: here.CityName,
                    Classification: classification.Name)
            ]);

        var response = await PostImportAsync(client, upload);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ImportCustomersResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.Equal("completed_with_errors", result.Status);
        Assert.Equal(7, result.TotalRows);
        Assert.Equal(1, result.ImportedCount);
        Assert.Equal(6, result.ErrorCount);
        Assert.Equal(2, result.Imported[0].RowNumber);
        Assert.Equal("Cliente Valido", result.Imported[0].Name);

        Assert.Contains(result.Errors, error =>
            error.RowNumber == 3 && error.Code == "customers.import.row.name_required");
        Assert.Contains(result.Errors, error =>
            error.RowNumber == 4 && error.Code == "customers.import.row.city_not_found");
        Assert.Contains(result.Errors, error =>
            error.RowNumber == 5 && error.Code == "customers.import.row.city_not_found");
        Assert.Contains(result.Errors, error =>
            error.RowNumber == 6 && error.Code == "customers.import.row.classification_not_found");
        Assert.Contains(result.Errors, error =>
            error.RowNumber == 7 && error.Code == "customers.import.row.duplicate_in_file");
        Assert.Contains(result.Errors, error =>
            error.RowNumber == 8 && error.Code == "customers.import.row.identification_taken");

        // `RowData` viaja para todas las filas con error, no solo `city_not_found`: es lo que
        // deja al modal de errores del frontend mostrar que habia puesto la persona y ofrecer
        // corregirlo ahi mismo, sin volver al Excel.
        var nameRequiredError = Assert.Single(
            result.Errors, error => error.RowNumber == 3);
        Assert.NotNull(nameRequiredError.RowData);
        Assert.Equal("900.222.222-2", nameRequiredError.RowData!.IdentificationNumber);

        var duplicateError = Assert.Single(
            result.Errors, error => error.RowNumber == 7);
        Assert.NotNull(duplicateError.RowData);
        Assert.Equal("Cliente Duplicado En Archivo", duplicateError.RowData!.Name);

        var identificationTakenError = Assert.Single(
            result.Errors, error => error.RowNumber == 8);
        Assert.NotNull(identificationTakenError.RowData);
        Assert.Equal("Cliente Ya Existe", identificationTakenError.RowData!.Name);

        // Nada se pierde: la unica fila valida se creo, y las seis invalidas no dejaron rastro.
        var page = await ListAsync(client, string.Empty);
        Assert.Equal(2, page.Items.Count); // el "existing" creado antes del import + la fila 2.
        Assert.Contains(page.Items, item => item.Name == "Cliente Valido");
    }

    /// <summary>
    /// Fase 8: una fila con la columna Cuc llena de un cliente existente lo actualiza en vez de
    /// crear uno nuevo — reemplazo total, mismas reglas que el PUT de un cliente.
    /// </summary>
    [Fact]
    public async Task ImportRowWithAnExistingCucUpdatesThatCustomerInsteadOfCreatingANewOne()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var manager = CreateManager(factory);
        using var client = CreateImporter(factory);
        var city = await EnsureCityAsync(client);
        var classification = await CreateClassificationAsync(manager);
        var existing = await CreateCustomerAsync(manager, city.CityId, classification.Id);

        using var upload = BuildExcelUpload(
            "clientes.xlsx",
            [
                new ExcelRowInput(
                    Cuc: existing.Cuc,
                    Name: "Nombre Actualizado",
                    IdentificationType: existing.IdentificationType,
                    IdentificationNumber: existing.IdentificationNumber,
                    Phone: "3009998877",
                    Department: city.DepartmentName,
                    City: city.CityName,
                    Classification: classification.Name)
            ]);

        var response = await PostImportAsync(client, upload);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ImportCustomersResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.Equal("completed", result.Status);
        Assert.Equal(1, result.ImportedCount);
        Assert.Equal("updated", result.Imported[0].Action);
        Assert.Equal(existing.Cuc, result.Imported[0].Cuc);

        // Ni un cliente nuevo (sigue habiendo uno solo) ni el Cuc cambio — solo el nombre y el
        // telefono, que es lo que la fila traia distinto.
        var page = await ListAsync(client, string.Empty);
        var item = Assert.Single(page.Items);
        Assert.Equal(existing.Cuc, item.Cuc);
        Assert.Equal("Nombre Actualizado", item.Name);
        Assert.Equal("3009998877", item.Phone);
    }

    /// <summary>
    /// El pedido es explicito: traer Cuc en una fila pide una actualizacion. Si no hay a quien
    /// actualizar, la fila falla — no cae a crear un cliente nuevo con ese Cuc inventado.
    /// </summary>
    [Fact]
    public async Task ImportRowWithACucThatMatchesNoCustomerFailsWithoutCreatingAnything()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var manager = CreateManager(factory);
        using var client = CreateImporter(factory);
        var city = await EnsureCityAsync(client);
        var classification = await CreateClassificationAsync(manager);

        using var upload = BuildExcelUpload(
            "clientes.xlsx",
            [
                new ExcelRowInput(
                    Cuc: "CLI08999999",
                    IdentificationNumber: "900.777.777-7",
                    Department: city.DepartmentName,
                    City: city.CityName,
                    Classification: classification.Name)
            ]);

        var response = await PostImportAsync(client, upload);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ImportCustomersResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.Equal("failed", result.Status);
        Assert.Equal(0, result.ImportedCount);
        Assert.Contains(result.Errors, error => error.Code == "customers.import.row.cuc_not_found");

        var page = await ListAsync(client, string.Empty);
        Assert.Empty(page.Items);
    }

    /// <summary>
    /// Una fila de actualizacion que conserva su propia identificacion no puede marcarse como
    /// "tomada por otro cliente" — el dueno existente de esa identificacion es el mismo cliente
    /// que la fila esta actualizando.
    /// </summary>
    [Fact]
    public async Task ImportRowUpdatingACustomerWithItsOwnIdentificationDoesNotFailAsTaken()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var manager = CreateManager(factory);
        using var client = CreateImporter(factory);
        var city = await EnsureCityAsync(client);
        var classification = await CreateClassificationAsync(manager);
        var existing = await CreateCustomerAsync(manager, city.CityId, classification.Id);

        using var upload = BuildExcelUpload(
            "clientes.xlsx",
            [
                new ExcelRowInput(
                    Cuc: existing.Cuc,
                    Name: existing.Name,
                    IdentificationType: existing.IdentificationType,
                    IdentificationNumber: existing.IdentificationNumber,
                    Department: city.DepartmentName,
                    City: city.CityName,
                    Classification: classification.Name)
            ]);

        var response = await PostImportAsync(client, upload);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ImportCustomersResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.Equal("completed", result.Status);
        Assert.Equal(1, result.ImportedCount);
        Assert.Empty(result.Errors);
    }

    // La celda Direccion dejo de ser opcional cuando nacio la libreta (028afe2): la fila crea la
    // direccion principal del cliente. Se rechaza como fila, con su codigo, y no como una
    // excepcion del dominio a mitad del archivo -- que se llevaria puesto el resto del lote.
    [Fact]
    public async Task ImportRowWithoutAnAddressIsRejectedWithItsOwnCode()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateImporter(factory);
        using var manager = CreateManager(factory);
        var here = await EnsureCityAsync(manager);
        var classification = await CreateClassificationAsync(manager);

        using var upload = BuildExcelUpload(
            "clientes.xlsx",
            [
                new ExcelRowInput(
                    Name: "Cliente Sin Direccion",
                    IdentificationNumber: "900.777.777-7",
                    Address: null,
                    Department: here.DepartmentName,
                    City: here.CityName,
                    Classification: classification.Name)
            ]);

        var response = await PostImportAsync(client, upload);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ImportCustomersResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.Equal(0, result.ImportedCount);
        Assert.Contains(result.Errors, error =>
            error.RowNumber == 2 && error.Code == "customers.import.row.address_required");
    }

    [Fact]
    public async Task ImportCustomersWithMissingColumnsIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateImporter(factory);

        var columnsWithoutDepartment = CustomerImportColumns.Ordered
            .Where(column => column != CustomerImportColumns.Department)
            .ToArray();
        using var upload = BuildExcelUpload(
            "clientes.xlsx",
            [new ExcelRowInput(Department: "Antioquia", City: "Medellin", Classification: "Mayorista")],
            columnsWithoutDepartment);

        var response = await PostImportAsync(client, upload);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("customers.import.file_invalid", body, StringComparison.Ordinal);

        var page = await ListAsync(client, string.Empty);
        Assert.Empty(page.Items);
    }

    [Fact]
    public async Task ImportCustomersWithNoDataRowsIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateImporter(factory);

        using var upload = BuildExcelUpload("clientes.xlsx", []);

        var response = await PostImportAsync(client, upload);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("customers.import.file_empty_data", body, StringComparison.Ordinal);

        var page = await ListAsync(client, string.Empty);
        Assert.Empty(page.Items);
    }

    [Theory]
    [InlineData("clientes.csv")]
    [InlineData("clientes.pdf")]
    [InlineData("clientes")]
    public async Task ImportRejectsAFileThatIsNotExcel(string fileName)
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateImporter(factory);

        using var upload = GarbageUpload(fileName);
        var response = await PostImportAsync(client, upload);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        Assert.Contains("customers.import.file_type_invalid", body, StringComparison.Ordinal);
    }

    // El permiso de importar es propio y **no** lo cubre `manage`: mil clientes de una vez no es la
    // misma autoridad que editar uno, y el gate CLI-00 pide mapearlo por separado.
    [Fact]
    public async Task ImportWithOnlyTheManagePermissionIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);

        using var upload = GarbageUpload("clientes.xlsx");
        var response = await PostImportAsync(client, upload);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// Fase 6: la plantilla trae exactamente las once columnas esperadas, en el orden del
    /// contrato — se parsea de vuelta con ClosedXML, no alcanza con comprobar el content-type.
    /// </summary>
    [Fact]
    public async Task DownloadTemplateReturnsAnExcelWithTheExpectedColumns()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateImporter(factory);

        var response = await client.GetAsync(
            $"{CustomersUrl()}/import/template", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            response.Content.Headers.ContentType?.MediaType);

        var bytes = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        using var workbook = new XLWorkbook(new MemoryStream(bytes));
        var sheet = workbook.Worksheets.First();
        var headerRow = sheet.Row(1);
        var lastColumn = headerRow.LastCellUsed()!.Address.ColumnNumber;
        var headers = Enumerable.Range(1, lastColumn)
            .Select(column => headerRow.Cell(column).GetString())
            .ToArray();

        Assert.Equal(CustomerImportColumns.Ordered, headers);
    }

    // Mismo permiso que /import: descargar la plantilla es parte del mismo flujo de carga masiva.
    [Fact]
    public async Task DownloadTemplateWithOnlyTheManagePermissionIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);

        var response = await client.GetAsync(
            $"{CustomersUrl()}/import/template", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// El modal de errores del frontend reenvia acá las filas que fallaron (tal cual las recibió
    /// de <c>/import</c>) para bajarlas en un Excel nuevo, ya cargado — mismo criterio de prueba
    /// que <see cref="DownloadTemplateReturnsAnExcelWithTheExpectedColumns"/>: se vuelve a parsear
    /// con ClosedXML, no alcanza con el content-type.
    /// </summary>
    [Fact]
    public async Task ExportFailedRowsReturnsAnExcelWithTheGivenRowsLoaded()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateImporter(factory);
        var rowData = new CustomerImportRowData(
            null,
            "Cliente A Corregir",
            null,
            "NIT",
            "900.999.999-9",
            null,
            null,
            null,
            "Antioquia",
            "Ciudad Que No Existe",
            "Clasificacion Que No Existe",
            null,
            null);

        var response = await client.PostAsJsonAsync(
            $"{CustomersUrl()}/import/failed-rows",
            new ExportFailedCustomerRowsRequest([rowData]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            response.Content.Headers.ContentType?.MediaType);

        var bytes = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        using var workbook = new XLWorkbook(new MemoryStream(bytes));
        var sheet = workbook.Worksheets.First();
        Assert.Equal("Cliente A Corregir", sheet.Cell(2, 2).GetString());
        Assert.Equal("900.999.999-9", sheet.Cell(2, 4).GetString());
        Assert.Equal("Ciudad Que No Existe", sheet.Cell(2, 9).GetString());
    }

    [Fact]
    public async Task ExportFailedRowsWithNoRowsIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateImporter(factory);

        var response = await client.PostAsJsonAsync(
            $"{CustomersUrl()}/import/failed-rows",
            new ExportFailedCustomerRowsRequest([]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("customers.import.export_empty", body, StringComparison.Ordinal);
    }

    // Mismo permiso que /import y que la plantilla vacia.
    [Fact]
    public async Task ExportFailedRowsWithOnlyTheManagePermissionIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var rowData = new CustomerImportRowData(
            null, "Cliente", null, "NIT", "900.000.000-0", null, null, null, "Antioquia",
            "Medellin", "X", null, null);

        var response = await client.PostAsJsonAsync(
            $"{CustomersUrl()}/import/failed-rows",
            new ExportFailedCustomerRowsRequest([rowData]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
