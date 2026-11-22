using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modules.Hr.Application;
using Npgsql;
using static Modules.Hr.IntegrationTests.HrApiHarness;
using static Modules.Hr.IntegrationTests.HrApiSeeds;

namespace Modules.Hr.IntegrationTests;

/// <summary>
/// La importación de empleados desde Excel de punta a punta (spec 2026-11-20, «Importación»): plantilla, vista previa que no
/// escribe y confirmación todo o nada. Los archivos se arman con ClosedXML en la prueba. El reloj del tenant está en 2026-11-20.
/// </summary>
public sealed class EmployeeImportApiTests : IClassFixture<HrApiFixture>
{
    private const string ExcelContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly HrApiFixture _fixture;

    public EmployeeImportApiTests(HrApiFixture fixture) => _fixture = fixture;

    /// <summary>Una fila válida: empleado indefinido, mensual, 2 500 000, ingreso 2026-01-15, sin retiro, con la empresa vacía.</summary>
    private static Dictionary<string, string?> Row(string document, params (string Header, string? Value)[] overrides)
    {
        var cells = HrImportColumns.Ordered.ToDictionary(header => header, _ => (string?)null);
        cells["NOMBRES"] = "Ana";
        cells["APELLIDOS"] = "Pérez";
        cells["TIPO DE DOCUMENTO"] = "Cédula de ciudadanía";
        cells["NÚMERO DE DOCUMENTO"] = document;
        cells["TIPO DE CONTRATO"] = "Empleado";
        cells["DURACIÓN"] = "Término indefinido";
        cells["TIPO DE SALARIO"] = "Mensual";
        cells["FECHA DE INGRESO"] = "2026-01-15";
        cells["SALARIO"] = "2500000";
        cells["CARGO"] = "Analista";
        cells["CENTRO DE TRABAJO"] = "Bodega - Principal";
        foreach (var (header, value) in overrides)
        {
            cells[header] = value;
        }

        return cells;
    }

    private static byte[] Workbook(params Dictionary<string, string?>[] rows) => Workbook(HrImportColumns.Ordered, rows);

    private static byte[] Workbook(IReadOnlyList<string> headers, IEnumerable<Dictionary<string, string?>> rows)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Empleados");
        for (var column = 0; column < headers.Count; column++)
        {
            sheet.Cell(1, column + 1).Value = headers[column];
        }

        var rowNumber = 2;
        foreach (var row in rows)
        {
            for (var column = 0; column < headers.Count; column++)
            {
                if (row.TryGetValue(headers[column], out var value) && value is not null)
                {
                    sheet.Cell(rowNumber, column + 1).Value = value;
                }
            }

            rowNumber++;
        }

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static MultipartFormDataContent Upload(byte[] content, string fileName = "planta.xlsx")
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new(ExcelContentType);
        form.Add(file, "file", fileName);
        return form;
    }

    private static async Task<HttpResponseMessage> PreviewAsync(HrTenant tenant, byte[] content, string fileName = "planta.xlsx") =>
        await tenant.Hr.PostAsync($"{tenant.Url}/employees/import/preview", Upload(content, fileName), Ct);

    private static async Task<HttpResponseMessage> ConfirmAsync(HrTenant tenant, byte[] content, string fileName = "planta.xlsx") =>
        await tenant.Hr.PostAsync($"{tenant.Url}/employees/import", Upload(content, fileName), Ct);

    private static async Task<JsonElement> OkAsync(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode == HttpStatusCode.OK, await BodyAsync(response));
        return await JsonOfAsync(response);
    }

    private static async Task<long> CountAsync(HrApiFixture fixture, string sql)
    {
        var connectionString = fixture.Factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("QepDatabase")!;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    private static Task<long> EmployeesOfAsync(HrApiFixture fixture, HrTenant tenant) =>
        CountAsync(fixture, $"SELECT count(*) FROM hr.employees WHERE tenant_id = '{tenant.Id}'");

    private static async Task<JsonElement> ImportedEmployeeAsync(HrTenant tenant, JsonElement result, int index)
    {
        var id = result.GetProperty("imported")[index].GetProperty("employeeId").GetGuid();
        var response = await tenant.Hr.GetAsync($"{tenant.Url}/employees/{id}", Ct);
        return await OkAsync(response);
    }

    [Fact]
    public async Task TheTemplateNeedsSetup()
    {
        using var tenant = await NewTenantAsync(_fixture);

        var response = await tenant.Hr.GetAsync($"{tenant.Url}/employees/import/template", Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("hr.settings.setup_incomplete", CodeOf(await JsonOfAsync(response)));
    }

    [Fact]
    public async Task TheTemplateHasTheHeadersAndTheTenantLists()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var archived = await CreatedAsync(
            tenant.Hr, $"{tenant.Url}/workplaces", new { name = "Taller", siteId = setup.SiteId, ciiuCode = "4711", riskClass = "III", highRiskPension = false });
        (await tenant.Hr.PostAsync($"{tenant.Url}/workplaces/{archived.GetProperty("id").GetGuid()}/archive", null, Ct)).EnsureSuccessStatusCode();
        var second = await SeedSecondCompanyAsync(tenant);
        await DeactivateCompanyAsync(tenant.Admin, tenant.Id, second.Company.Id);

        var response = await tenant.Hr.GetAsync($"{tenant.Url}/employees/import/template", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "plantilla-empleados.xlsx",
            response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        await using var stream = await response.Content.ReadAsStreamAsync(Ct);
        using var workbook = new XLWorkbook(stream);
        var data = workbook.Worksheet("Empleados");
        var values = workbook.Worksheet("Valores válidos");

        Assert.Equal(35, HrImportColumns.Ordered.Count);
        Assert.DoesNotContain("CAJA DE COMPENSACIÓN", HrImportColumns.Ordered);
        for (var column = 0; column < 35; column++)
        {
            Assert.Equal(HrImportColumns.Ordered[column], data.Cell(1, column + 1).GetString());
        }

        Assert.Equal("PAÍS", data.Cell(1, 5).GetString());
        Assert.Equal("EMPRESA", data.Cell(1, 23).GetString());
        Assert.Equal(["Bodega - Principal"], ListUnder(values, "Centros de trabajo"));
        Assert.Equal([$"{setup.Company.Name} - {setup.Company.TaxId}"], ListUnder(values, "Empresas"));
        Assert.Contains("Colombia", ListUnder(values, "Países"));
        Assert.Equal(["Analista"], ListUnder(values, "Cargos"));
        Assert.Equal(["Operaciones"], ListUnder(values, "Áreas"));
        Assert.Contains("Término fijo", ListUnder(values, "Duraciones"));
    }

    private static List<string> ListUnder(IXLWorksheet sheet, string title)
    {
        var column = sheet.Row(1).CellsUsed().Single(cell => cell.GetString() == title).Address.ColumnNumber;
        var list = new List<string>();
        for (var row = 2; !sheet.Cell(row, column).IsEmpty(); row++)
        {
            list.Add(sheet.Cell(row, column).GetString());
        }

        return list;
    }

    [Fact]
    public async Task TheTemplateNeedsAnActiveCompany()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        await DeactivateCompanyAsync(tenant.Admin, tenant.Id, setup.Company.Id);

        var response = await tenant.Hr.GetAsync($"{tenant.Url}/employees/import/template", Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("hr.settings.setup_incomplete", CodeOf(await JsonOfAsync(response)));
    }

    [Fact]
    public async Task PreviewShowsTheCompanyOfEachRow()
    {
        using var tenant = await NewTenantAsync(_fixture);
        await SeedSetupAsync(tenant);

        var preview = await OkAsync(await PreviewAsync(tenant, Workbook(Row("1001"))));

        var first = preview.GetProperty("validRows")[0];
        Assert.Equal("Comercializadora QEP S.A.S.", first.GetProperty("company").GetString());
        Assert.Equal("Bodega - Principal", first.GetProperty("workplace").GetString());
        Assert.Equal("Analista", first.GetProperty("position").GetString());
        Assert.Equal("Active", first.GetProperty("contractStatus").GetString());
    }

    [Fact]
    public async Task ImportedEmployeesKeepCountryAndCompany()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);

        var result = await OkAsync(await ConfirmAsync(
            tenant, Workbook(Row("9001", ("TIPO DE DOCUMENTO", "Permiso por protección temporal"), ("PAÍS", "Venezuela")))));

        Assert.Equal(1, result.GetProperty("importedCount").GetInt32());
        var detail = await ImportedEmployeeAsync(tenant, result, 0);
        Assert.Equal("VE", detail.GetProperty("employee").GetProperty("country").GetString());
        Assert.Equal(setup.Company.Id, detail.GetProperty("contract").GetProperty("company").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task PreviewReportsValidRowsErrorsWarningsAndSummaryWithoutWriting()
    {
        using var tenant = await NewTenantAsync(_fixture);
        await SeedSetupAsync(tenant);
        var file = Workbook(
            Row("1001"),
            Row("1002", ("FECHA DE RETIRO", "2026-10-31"), ("MOTIVO DE RETIRO", "Renuncia")),
            Row("1003", ("SALARIO", "abc")),
            Row("1004", ("SALARIO", "1500000"), ("FECHA DE INGRESO", "2026-02-01")));

        var preview = await OkAsync(await PreviewAsync(tenant, file));

        Assert.Equal("planta.xlsx", preview.GetProperty("fileName").GetString());
        Assert.Equal(4, preview.GetProperty("totalRows").GetInt32());
        Assert.Equal(3, preview.GetProperty("validRows").GetArrayLength());
        var error = Assert.Single(preview.GetProperty("errors").EnumerateArray());
        Assert.Equal("hr.import.row.salary_invalid", error.GetProperty("code").GetString());
        Assert.Equal("salary", error.GetProperty("field").GetString());
        Assert.Equal(4, error.GetProperty("rowNumber").GetInt32());
        var warning = Assert.Single(preview.GetProperty("warnings").EnumerateArray());
        Assert.Equal("hr.import.row.below_minimum", warning.GetProperty("code").GetString());
        var summary = preview.GetProperty("summary");
        Assert.Equal(3, summary.GetProperty("employeesToCreate").GetInt32());
        Assert.Equal(2, summary.GetProperty("activeContracts").GetInt32());
        Assert.Equal(1, summary.GetProperty("terminatedContracts").GetInt32());
        Assert.Equal(0, await EmployeesOfAsync(_fixture, tenant));
    }

    [Fact]
    public async Task ARepeatedDocumentMarksBothRows()
    {
        using var tenant = await NewTenantAsync(_fixture);
        await SeedSetupAsync(tenant);

        var preview = await OkAsync(await PreviewAsync(tenant, Workbook(Row("1001"), Row("1001"), Row("1002"))));

        var errors = preview.GetProperty("errors").EnumerateArray().ToList();
        Assert.Equal([2, 3], errors.Select(error => error.GetProperty("rowNumber").GetInt32()));
        Assert.All(errors, error =>
        {
            Assert.Equal("hr.import.row.document_duplicated_in_file", error.GetProperty("code").GetString());
            Assert.Equal("documentNumber", error.GetProperty("field").GetString());
        });
        Assert.Equal(1, preview.GetProperty("validRows").GetArrayLength());
    }

    [Fact]
    public async Task AnExistingDocumentIsDocumentTaken()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        await HireAsync(tenant, setup, "5005");

        var preview = await OkAsync(await PreviewAsync(tenant, Workbook(Row("5005"), Row("5006"))));

        var error = Assert.Single(preview.GetProperty("errors").EnumerateArray());
        Assert.Equal("hr.import.row.document_taken", error.GetProperty("code").GetString());
        Assert.Equal(2, error.GetProperty("rowNumber").GetInt32());
        Assert.Equal(1, preview.GetProperty("validRows").GetArrayLength());
    }

    [Fact]
    public async Task ImportCreatesEmployeesContractsAndRecords()
    {
        using var tenant = await NewTenantAsync(_fixture);
        await SeedSetupAsync(tenant);
        var file = Workbook(
            Row("2001", ("FECHA DE RETIRO", "2026-10-31"), ("MOTIVO DE RETIRO", "Renuncia")),
            Row("2002", ("SALARIO", "1500000"), ("FECHA DE INGRESO", "2026-02-01")));

        var result = await OkAsync(await ConfirmAsync(tenant, file));

        Assert.Equal(2, result.GetProperty("importedCount").GetInt32());
        Assert.Equal([2, 3], result.GetProperty("imported").EnumerateArray().Select(row => row.GetProperty("rowNumber").GetInt32()));
        var retired = await ImportedEmployeeAsync(tenant, result, 0);
        Assert.Equal("Retired", retired.GetProperty("employee").GetProperty("status").GetString());
        Assert.Equal("Resignation", retired.GetProperty("previousContracts")[0].GetProperty("termination").GetProperty("reason").GetString());
        var warned = await ImportedEmployeeAsync(tenant, result, 1);
        Assert.True(warned.GetProperty("contract").GetProperty("salaries")[0].GetProperty("belowMinimumAcknowledged").GetBoolean());
        foreach (var row in result.GetProperty("imported").EnumerateArray())
        {
            Assert.Equal(1, await CountAuditEntriesAsync(_fixture.Factory, "hr.employee.imported", row.GetProperty("employeeId").GetGuid().ToString()));
        }
    }

    [Fact]
    public async Task ConfirmingWithErrorsIs422RowsInvalidAndWritesNothing()
    {
        using var tenant = await NewTenantAsync(_fixture);
        await SeedSetupAsync(tenant);

        var response = await ConfirmAsync(tenant, Workbook(Row("3001"), Row("3002", ("SALARIO", "abc"))));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("hr.import.rows_invalid", CodeOf(await JsonOfAsync(response)));
        Assert.Equal(0, await EmployeesOfAsync(_fixture, tenant));
    }

    [Fact]
    public async Task ConfirmingWithOnlyWarningsImports()
    {
        using var tenant = await NewTenantAsync(_fixture);
        await SeedSetupAsync(tenant);

        var result = await OkAsync(await ConfirmAsync(
            tenant, Workbook(Row("4001", ("SALARIO", "1500000"), ("FECHA DE INGRESO", "2026-02-01")))));

        Assert.Equal(1, result.GetProperty("importedCount").GetInt32());
        Assert.Equal(1, await EmployeesOfAsync(_fixture, tenant));
    }

    [Fact]
    public async Task ADocumentCreatedBetweenPreviewAndConfirmWritesNothing()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var file = Workbook(Row("6001"), Row("6002"));

        var preview = await OkAsync(await PreviewAsync(tenant, file));
        Assert.Empty(preview.GetProperty("errors").EnumerateArray());
        Assert.Equal(2, preview.GetProperty("validRows").GetArrayLength());

        await HireAsync(tenant, setup, "6002");
        var response = await ConfirmAsync(tenant, file);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("hr.import.rows_invalid", CodeOf(await JsonOfAsync(response)));
        Assert.Equal(1, await EmployeesOfAsync(_fixture, tenant));
    }

    [Theory]
    [InlineData("NoFile", "hr.import.file_required", "Adjunta un archivo .xlsx.")]
    [InlineData("Xls", "hr.import.extension_invalid", "Sólo se aceptan archivos .xlsx.")]
    [InlineData("TooLarge", "hr.import.too_large", "El archivo pasa de 5 MB.")]
    [InlineData("Garbage", "hr.import.unreadable", "No pudimos leer el archivo como un libro de Excel.")]
    [InlineData("OtherHeaders", "hr.import.header_mismatch", "Las columnas no coinciden con la plantilla. Descarga la plantilla de nuevo.")]
    [InlineData("OnlyHeaders", "hr.import.empty", "El archivo no tiene filas con datos.")]
    [InlineData("TooManyRows", "hr.import.too_many_rows", "El archivo tiene más de 1000 filas.")]
    public async Task FileErrors(string kind, string expectedCode, string expectedMessage)
    {
        using var tenant = await NewTenantAsync(_fixture);
        await SeedSetupAsync(tenant);

        var response = kind switch
        {
            "NoFile" => await tenant.Hr.PostAsync(
                $"{tenant.Url}/employees/import/preview", new MultipartFormDataContent { { new StringContent("x"), "other" } }, Ct),
            "Xls" => await PreviewAsync(tenant, Workbook(Row("1")), "planta.xls"),
            "TooLarge" => await PreviewAsync(tenant, new byte[(5 * 1024 * 1024) + 1]),
            "Garbage" => await PreviewAsync(tenant, [1, 2, 3, 4, 5, 6, 7, 8]),
            "OtherHeaders" => await PreviewAsync(tenant, Workbook(["A", "B"], [new Dictionary<string, string?> { ["A"] = "x" }])),
            "OnlyHeaders" => await PreviewAsync(tenant, Workbook()),
            _ => await PreviewAsync(tenant, Workbook(Enumerable.Range(0, 1001).Select(index => Row($"7{index:D5}")).ToArray())),
        };

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await JsonOfAsync(response);
        Assert.Equal(expectedCode, CodeOf(problem));
        Assert.Equal(expectedMessage, problem.GetProperty("detail").GetString());
    }

    // ClosedXML serializa los números con 15 cifras: se reescribe el valor del xml para que la celda numérica traiga los 16 dígitos exactos.
    private static byte[] ExactNumber(byte[] workbook, string cellReference, string digits)
    {
        using var source = new MemoryStream(workbook);
        using var output = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(source, System.IO.Compression.ZipArchiveMode.Update, leaveOpen: true))
        {
            var entry = archive.GetEntry("xl/worksheets/sheet1.xml")!;
            string xml;
            using (var reader = new StreamReader(entry.Open()))
            {
                xml = reader.ReadToEnd();
            }

            var patched = System.Text.RegularExpressions.Regex.Replace(
                xml, $"(<(?:x:)?c r=\"{cellReference}\"[^>]*><(?:x:)?v>)[^<]*(</(?:x:)?v>)", $"${{1}}{digits}${{2}}");
            Assert.NotEqual(xml, patched);
            entry.Delete();
            using var writer = new StreamWriter(archive.CreateEntry("xl/worksheets/sheet1.xml").Open());
            writer.Write(patched);
        }

        source.Position = 0;
        source.CopyTo(output);
        return output.ToArray();
    }

    [Fact]
    public async Task NumericCellsKeepTheirDisplayedDigits()
    {
        using var tenant = await NewTenantAsync(_fixture);
        await SeedSetupAsync(tenant);
        var row = Row("x", ("TIPO DE DOCUMENTO", "Cédula de ciudadanía"));
        byte[] file;
        using (var workbook = new XLWorkbook())
        {
            var sheet = workbook.Worksheets.Add("Empleados");
            for (var column = 0; column < HrImportColumns.Ordered.Count; column++)
            {
                sheet.Cell(1, column + 1).Value = HrImportColumns.Ordered[column];
                if (row.GetValueOrDefault(HrImportColumns.Ordered[column]) is { } text)
                {
                    sheet.Cell(2, column + 1).Value = text;
                }
            }

            // Sin el formato de texto de la plantilla: un número con ceros a la izquierda y una cuenta de 16 dígitos.
            var document = sheet.Cell(2, HrImportColumns.Ordered.ToList().IndexOf(HrImportColumns.DocumentNumber) + 1);
            document.Value = 1234;
            document.Style.NumberFormat.Format = "00000000";
            var method = sheet.Cell(2, HrImportColumns.Ordered.ToList().IndexOf(HrImportColumns.PaymentMethod) + 1);
            method.Value = "Transferencia bancaria";
            sheet.Cell(2, HrImportColumns.Ordered.ToList().IndexOf(HrImportColumns.BankName) + 1).Value = "Bancolombia";
            sheet.Cell(2, HrImportColumns.Ordered.ToList().IndexOf(HrImportColumns.BankAccountType) + 1).Value = "Ahorros";
            var account = sheet.Cell(2, HrImportColumns.Ordered.ToList().IndexOf(HrImportColumns.BankAccountNumber) + 1);
            account.Value = 1234567890123456L;
            account.Style.NumberFormat.Format = "0";
            using var stream = new MemoryStream();
            workbook.SaveAs(stream);
            file = ExactNumber(stream.ToArray(), "V2", "1234567890123456");
        }

        var result = await OkAsync(await ConfirmAsync(tenant, file));

        var detail = await ImportedEmployeeAsync(tenant, result, 0);
        Assert.Equal("00001234", detail.GetProperty("employee").GetProperty("documentNumber").GetString());
        Assert.Equal("1234567890123456", detail.GetProperty("employee").GetProperty("payment").GetProperty("bankAccountNumber").GetString());
    }

    [Fact]
    public async Task WithoutContractManageTheImportIs403()
    {
        using var tenant = await NewTenantAsync(_fixture);
        await SeedSetupAsync(tenant);
        using var reader = tenant.Client(HrPermissions.EmployeeRead, HrPermissions.EmployeeManage, HrPermissions.ContractRead);
        var file = Workbook(Row("8001"));

        var template = await reader.GetAsync($"{tenant.Url}/employees/import/template", Ct);
        var preview = await reader.PostAsync($"{tenant.Url}/employees/import/preview", Upload(file), Ct);
        var confirm = await reader.PostAsync($"{tenant.Url}/employees/import", Upload(file), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, template.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, preview.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, confirm.StatusCode);
        Assert.Equal(0, await EmployeesOfAsync(_fixture, tenant));
    }
}
