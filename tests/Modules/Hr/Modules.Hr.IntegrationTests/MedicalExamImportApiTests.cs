using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modules.Hr.Application;
using Modules.Hr.Application.MedicalExams;
using Npgsql;
using static Modules.Hr.IntegrationTests.HrApiHarness;
using static Modules.Hr.IntegrationTests.HrApiSeeds;

namespace Modules.Hr.IntegrationTests;

/// <summary>
/// Spec 2026-11-23 §8: the import of the exam history from Excel. The preview writes nothing, one bad row writes nothing, and the
/// same file twice never duplicates. The files are built with ClosedXML in the test. The clock of the tenant is 2026-11-20.
/// </summary>
public sealed class MedicalExamImportApiTests : IClassFixture<HrApiFixture>
{
    private const string ExcelContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly HrApiFixture _fixture;

    public MedicalExamImportApiTests(HrApiFixture fixture) => _fixture = fixture;

    private static Dictionary<string, string?> Row(string document, params (string Header, string? Value)[] overrides)
    {
        var cells = MedicalExamImportColumns.Ordered.ToDictionary(header => header, _ => (string?)null);
        cells["TIPO DOCUMENTO"] = "Cédula de ciudadanía";
        cells["NÚMERO DOCUMENTO"] = document;
        cells["TIPO DE EXAMEN"] = "Periódico";
        cells["FECHA DEL EXAMEN"] = "2026-10-15";
        foreach (var (header, value) in overrides)
        {
            cells[header] = value;
        }

        return cells;
    }

    private static byte[] Workbook(params Dictionary<string, string?>[] rows)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Exámenes");
        var headers = MedicalExamImportColumns.Ordered;
        for (var column = 0; column < headers.Count; column++)
        {
            sheet.Cell(1, column + 1).Value = headers[column];
        }

        for (var index = 0; index < rows.Length; index++)
        {
            for (var column = 0; column < headers.Count; column++)
            {
                if (rows[index].TryGetValue(headers[column], out var value) && value is not null)
                {
                    sheet.Cell(index + 2, column + 1).Value = value;
                }
            }
        }

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static MultipartFormDataContent Upload(byte[] content, string fileName = "examenes.xlsx")
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new(ExcelContentType);
        form.Add(file, "file", fileName);
        return form;
    }

    private static Task<HttpResponseMessage> PreviewAsync(HttpClient client, HrTenant tenant, byte[] content, string fileName = "examenes.xlsx") =>
        client.PostAsync($"{tenant.Url}/medical-exams/import/preview", Upload(content, fileName), Ct);

    private static Task<HttpResponseMessage> ImportAsync(HttpClient client, HrTenant tenant, byte[] content, string fileName = "examenes.xlsx") =>
        client.PostAsync($"{tenant.Url}/medical-exams/import", Upload(content, fileName), Ct);

    private static string ContractUrl(HrTenant tenant, JsonElement detail) =>
        $"{EmployeeUrl(tenant, detail)}/contracts/{detail.GetProperty("contract").GetProperty("id").GetGuid()}";

    private static async Task<JsonElement> ExamsOfAsync(HrTenant tenant, JsonElement detail)
    {
        var response = await tenant.Hr.GetAsync($"{EmployeeUrl(tenant, detail)}/medical-exams", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await JsonOfAsync(response);
    }

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.True(response.StatusCode == status, await BodyAsync(response));
        return await JsonOfAsync(response);
    }

    private async Task<(HrTenant Tenant, JsonElement First, JsonElement Second)> SeedAsync()
    {
        var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var first = await HireAsync(tenant, HireBody(setup, documentNumber: "9100000001", firstName: "Ana", lastName: "Pérez"));
        var second = await HireAsync(tenant, HireBody(setup, documentNumber: "9100000002", firstName: "Luis", lastName: "Gómez"));
        return (tenant, first, second);
    }

    private static List<string> ErrorCodes(JsonElement preview) =>
        preview.GetProperty("errors").EnumerateArray().Select(error => $"{error.GetProperty("rowNumber").GetInt32()}:{error.GetProperty("code").GetString()}").ToList();

    [Fact]
    public async Task PreviewWritesNothing()
    {
        var (tenant, first, second) = await SeedAsync();
        using var _ = tenant;

        var response = await PreviewAsync(tenant.Hr, tenant, Workbook(Row("9100000001"), Row("9100000002", ("TIPO DE EXAMEN", "Preingreso"))));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var preview = await JsonOfAsync(response);
        Assert.Equal(2, preview.GetProperty("validRows").GetArrayLength());
        Assert.Equal(0, preview.GetProperty("errors").GetArrayLength());
        Assert.Equal(2, preview.GetProperty("totalRows").GetInt32());
        Assert.Equal(2, preview.GetProperty("summary").GetProperty("examsToCreate").GetInt32());
        Assert.Equal("Ana Pérez", preview.GetProperty("validRows")[0].GetProperty("employeeName").GetString());
        Assert.Empty((await ExamsOfAsync(tenant, first)).GetProperty("exams").EnumerateArray());
        Assert.Empty((await ExamsOfAsync(tenant, second)).GetProperty("exams").EnumerateArray());
    }

    [Fact]
    public async Task ImportAllOrNothingOneBadRowWritesNone()
    {
        var (tenant, first, second) = await SeedAsync();
        using var _ = tenant;
        var file = Workbook(Row("9100000001"), Row("9100000002", ("TIPO DE EXAMEN", "Chequeo")));

        var preview = await ProblemAsync(await PreviewAsync(tenant.Hr, tenant, file), HttpStatusCode.OK);
        var response = await ImportAsync(tenant.Hr, tenant, file);

        Assert.Equal(["3:hr.medical_exam.import.row.exam_type_invalid"], ErrorCodes(preview));
        Assert.Equal(1, preview.GetProperty("validRows").GetArrayLength());
        var problem = await ProblemAsync(response, HttpStatusCode.UnprocessableEntity);
        Assert.Equal("hr.import.rows_invalid", CodeOf(problem));
        Assert.Empty((await ExamsOfAsync(tenant, first)).GetProperty("exams").EnumerateArray());
        Assert.Empty((await ExamsOfAsync(tenant, second)).GetProperty("exams").EnumerateArray());
    }

    [Fact]
    public async Task ImportStoresTheRowsWithTheirDataAndAuditsOnceWithTheCount()
    {
        var (tenant, first, second) = await SeedAsync();
        using var _ = tenant;
        var file = Workbook(
            Row(
                "9100000001",
                ("FECHA DEL EXAMEN", "15/10/2026"),
                ("IPS / MÉDICO", "IPS Norte"),
                ("RESTRICCIONES", "No lifting"),
                ("RECOMENDACIONES", "Pauses"),
                ("RESTRICCIONES HASTA", "2026-12-01")),
            Row("9100000002", ("TIPO DE EXAMEN", "Cambio de ocupación")));

        var response = await ImportAsync(tenant.Hr, tenant, file);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await JsonOfAsync(response);
        Assert.Equal(2, result.GetProperty("importedCount").GetInt32());
        var exam = (await ExamsOfAsync(tenant, first)).GetProperty("exams")[0];
        Assert.Equal("Periodic", exam.GetProperty("type").GetString());
        Assert.Equal("2026-10-15", exam.GetProperty("examDate").GetString());
        Assert.Equal("IPS Norte", exam.GetProperty("provider").GetString());
        Assert.Equal("No lifting", exam.GetProperty("restrictions").GetString());
        Assert.Equal("Pauses", exam.GetProperty("recommendations").GetString());
        Assert.Equal("2026-12-01", exam.GetProperty("restrictionsUntil").GetString());
        Assert.Equal("OccupationChange", (await ExamsOfAsync(tenant, second)).GetProperty("exams")[0].GetProperty("type").GetString());

        // One audit entry for the whole file, never one per row.
        var connectionString = _fixture.Factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("QepDatabase")!;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var count = new NpgsqlCommand(
            "SELECT count(*) FROM audit.entries WHERE action = 'hr.medical_exam.imported' AND tenant_id = @tenant", connection);
        count.Parameters.AddWithValue("tenant", tenant.Id);
        Assert.Equal(1L, (long)(await count.ExecuteScalarAsync(Ct))!);
    }

    [Fact]
    public async Task ImportTwiceSecondRunIsAllDuplicates()
    {
        var (tenant, first, second) = await SeedAsync();
        using var _ = tenant;
        var file = Workbook(Row("9100000001"), Row("9100000002"));
        Assert.Equal(HttpStatusCode.OK, (await ImportAsync(tenant.Hr, tenant, file)).StatusCode);

        var preview = await ProblemAsync(await PreviewAsync(tenant.Hr, tenant, file), HttpStatusCode.OK);
        var again = await ImportAsync(tenant.Hr, tenant, file);

        Assert.Equal(["2:hr.medical_exam.import.row.duplicate", "3:hr.medical_exam.import.row.duplicate"], ErrorCodes(preview));
        Assert.Equal("hr.import.rows_invalid", CodeOf(await ProblemAsync(again, HttpStatusCode.UnprocessableEntity)));
        Assert.Single((await ExamsOfAsync(tenant, first)).GetProperty("exams").EnumerateArray());
        Assert.Single((await ExamsOfAsync(tenant, second)).GetProperty("exams").EnumerateArray());
    }

    [Fact]
    public async Task ImportDuplicateInsideTheFileIsRowError()
    {
        var (tenant, first, _) = await SeedAsync();
        using var _ = tenant;
        var file = Workbook(Row("9100000001"), Row("9100000002"), Row("9100000001", ("RESTRICCIONES", "Another text")));

        var preview = await ProblemAsync(await PreviewAsync(tenant.Hr, tenant, file), HttpStatusCode.OK);
        var response = await ImportAsync(tenant.Hr, tenant, file);

        Assert.Equal(["2:hr.medical_exam.import.row.duplicated_in_file", "4:hr.medical_exam.import.row.duplicated_in_file"], ErrorCodes(preview));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Empty((await ExamsOfAsync(tenant, first)).GetProperty("exams").EnumerateArray());
    }

    [Fact]
    public async Task ImportUnknownDocumentIsRowError()
    {
        var (tenant, _, _) = await SeedAsync();
        using var _ = tenant;
        var file = Workbook(
            Row("9100000001"),
            Row("0000000000"),
            Row("9100000002", ("TIPO DOCUMENTO", "Pasaporte")),
            Row("9100000002", ("TIPO DOCUMENTO", "Otro")),
            Row(string.Empty));

        var preview = await ProblemAsync(await PreviewAsync(tenant.Hr, tenant, file), HttpStatusCode.OK);

        Assert.Equal(
            [
                "3:hr.medical_exam.import.row.employee_not_found",
                "4:hr.medical_exam.import.row.employee_not_found",
                "5:hr.medical_exam.import.row.document_type_invalid",
                "6:hr.medical_exam.import.row.document_number_required",
            ],
            ErrorCodes(preview));
    }

    [Fact]
    public async Task ImportFutureDateIsRowError()
    {
        var (tenant, _, _) = await SeedAsync();
        using var _ = tenant;
        var file = Workbook(
            Row("9100000001", ("FECHA DEL EXAMEN", "2026-11-21")),
            Row("9100000002", ("FECHA DEL EXAMEN", "not a date")),
            Row("9100000002", ("TIPO DE EXAMEN", "Seguimiento"), ("FECHA DEL EXAMEN", "2026-11-20")));

        var preview = await ProblemAsync(await PreviewAsync(tenant.Hr, tenant, file), HttpStatusCode.OK);

        Assert.Equal(
            ["2:hr.medical_exam.import.row.exam_date_in_future", "3:hr.medical_exam.import.row.exam_date_invalid"],
            ErrorCodes(preview));
        Assert.Equal(1, preview.GetProperty("validRows").GetArrayLength());
    }

    [Fact]
    public async Task ImportRestrictionsUntilWithoutRestrictionsIsRowError()
    {
        var (tenant, _, _) = await SeedAsync();
        using var _ = tenant;
        var file = Workbook(
            Row("9100000001", ("RESTRICCIONES HASTA", "2026-12-01")),
            Row("9100000002", ("RESTRICCIONES", "No lifting"), ("RESTRICCIONES HASTA", "2026-10-01")),
            Row("9100000002", ("TIPO DE EXAMEN", "Seguimiento"), ("RESTRICCIONES", "No lifting"), ("RESTRICCIONES HASTA", "garbage")));

        var preview = await ProblemAsync(await PreviewAsync(tenant.Hr, tenant, file), HttpStatusCode.OK);

        Assert.Equal(
            [
                "2:hr.medical_exam.import.row.restrictions_until_invalid",
                "3:hr.medical_exam.import.row.restrictions_until_invalid",
                "4:hr.medical_exam.import.row.restrictions_until_invalid",
            ],
            ErrorCodes(preview));
    }

    [Fact]
    public async Task ImportTextTooLongIsRowError()
    {
        var (tenant, _, _) = await SeedAsync();
        using var _ = tenant;
        var file = Workbook(Row("9100000001", ("RESTRICCIONES", new string('x', 2001))), Row("9100000002", ("IPS / MÉDICO", new string('x', 201))));

        var preview = await ProblemAsync(await PreviewAsync(tenant.Hr, tenant, file), HttpStatusCode.OK);

        Assert.Equal(["2:hr.medical_exam.import.row.text_too_long", "3:hr.medical_exam.import.row.text_too_long"], ErrorCodes(preview));
    }

    // A text over its limit is a row error and writes nothing: never a silent drop, and no second error invented by the drop (the
    // restrictions end date is still judged against the restrictions that were typed).
    [Fact]
    public async Task ImportTooLongRestrictionsIsASingleRowErrorAndWritesNothing()
    {
        var (tenant, first, second) = await SeedAsync();
        using var _ = tenant;
        var file = Workbook(
            Row("9100000001", ("RESTRICCIONES", new string('x', 2001)), ("RESTRICCIONES HASTA", "2026-12-01")),
            Row("9100000002", ("RECOMENDACIONES", new string('y', 2001))),
            Row("9100000002", ("TIPO DE EXAMEN", "Seguimiento"), ("IPS / MÉDICO", new string('z', 201))));

        var preview = await ProblemAsync(await PreviewAsync(tenant.Hr, tenant, file), HttpStatusCode.OK);
        var response = await ImportAsync(tenant.Hr, tenant, file);

        Assert.Equal(
            ["2:hr.medical_exam.import.row.text_too_long", "3:hr.medical_exam.import.row.text_too_long", "4:hr.medical_exam.import.row.text_too_long"],
            ErrorCodes(preview));
        Assert.Equal(0, preview.GetProperty("validRows").GetArrayLength());
        Assert.Equal("restrictions", preview.GetProperty("errors")[0].GetProperty("field").GetString());
        Assert.False(string.IsNullOrWhiteSpace(preview.GetProperty("errors")[0].GetProperty("message").GetString()));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Empty((await ExamsOfAsync(tenant, first)).GetProperty("exams").EnumerateArray());
        Assert.Empty((await ExamsOfAsync(tenant, second)).GetProperty("exams").EnumerateArray());
    }
    [Fact]
    public async Task ImportExitRowClosesPendingRequirement()
    {
        var (tenant, first, second) = await SeedAsync();
        using var _ = tenant;
        await tenant.Hr.PostAsJsonAsync($"{ContractUrl(tenant, first)}/terminate", new { reason = "Resignation", date = "2026-11-16", notes = (string?)null }, Ct);
        await tenant.Hr.PostAsJsonAsync($"{ContractUrl(tenant, second)}/terminate", new { reason = "Resignation", date = "2026-11-16", notes = (string?)null }, Ct);
        // The first exit exam is dated after the termination; the second one before it, so it is history and closes nothing.
        var file = Workbook(
            Row("9100000001", ("TIPO DE EXAMEN", "Egreso"), ("FECHA DEL EXAMEN", "2026-11-18")),
            Row("9100000002", ("TIPO DE EXAMEN", "Egreso"), ("FECHA DEL EXAMEN", "2026-11-10")));

        var response = await ImportAsync(tenant.Hr, tenant, file);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var closed = await ExamsOfAsync(tenant, first);
        Assert.Equal("Done", closed.GetProperty("exitRequirement").GetProperty("status").GetString());
        Assert.Equal(closed.GetProperty("exams")[0].GetProperty("id").GetGuid(), closed.GetProperty("exitRequirement").GetProperty("examId").GetGuid());
        var open = await ExamsOfAsync(tenant, second);
        Assert.Equal("Pending", open.GetProperty("exitRequirement").GetProperty("status").GetString());
        Assert.Single(open.GetProperty("exams").EnumerateArray());
    }

    [Fact]
    public async Task ImportExitRowForAnEmployeeWithoutTerminationIsHistory()
    {
        var (tenant, first, _) = await SeedAsync();
        using var _ = tenant;

        var response = await ImportAsync(tenant.Hr, tenant, Workbook(Row("9100000001", ("TIPO DE EXAMEN", "Egreso"), ("FECHA DEL EXAMEN", "2025-06-01"))));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await ExamsOfAsync(tenant, first);
        Assert.Equal("Exit", dto.GetProperty("exams")[0].GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Null, dto.GetProperty("exitRequirement").ValueKind);
    }

    [Fact]
    public async Task TemplateHasTheEightColumnsAndTheValidValuesSheet()
    {
        using var tenant = await NewTenantAsync(_fixture);

        var response = await tenant.Hr.GetAsync($"{tenant.Url}/medical-exams/import/template", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ExcelContentType, response.Content.Headers.ContentType?.MediaType);
        using var stream = new MemoryStream(await response.Content.ReadAsByteArrayAsync(Ct));
        using var workbook = new XLWorkbook(stream);
        var data = workbook.Worksheet("Exámenes");
        Assert.Equal(
            [
                "TIPO DOCUMENTO", "NÚMERO DOCUMENTO", "TIPO DE EXAMEN", "FECHA DEL EXAMEN", "IPS / MÉDICO", "RESTRICCIONES",
                "RECOMENDACIONES", "RESTRICCIONES HASTA",
            ],
            Enumerable.Range(1, 8).Select(column => data.Cell(1, column).GetString()));
        Assert.Equal(string.Empty, data.Cell(1, 9).GetString());
        var values = workbook.Worksheet("Valores válidos");
        var examTypes = Enumerable.Range(2, 7).Select(row => values.Cell(row, 2).GetString()).ToList();
        Assert.Equal(
            ["Preingreso", "Periódico", "Cambio de ocupación", "Post-incapacidad", "Retorno laboral", "Seguimiento", "Egreso"],
            examTypes);
        Assert.Equal("Cédula de ciudadanía", values.Cell(2, 1).GetString());
    }

    [Fact]
    public async Task ImportFileErrorsAreReported()
    {
        var (tenant, _, _) = await SeedAsync();
        using var _ = tenant;

        var none = await tenant.Hr.PostAsync($"{tenant.Url}/medical-exams/import/preview", new MultipartFormDataContent { { new StringContent("x"), "other" } }, Ct);
        var xls = await PreviewAsync(tenant.Hr, tenant, Workbook(Row("9100000001")), "examenes.xls");
        var garbage = await PreviewAsync(tenant.Hr, tenant, [1, 2, 3, 4, 5, 6, 7, 8]);
        var headers = await PreviewAsync(tenant.Hr, tenant, EmployeeWorkbook());
        var empty = await PreviewAsync(tenant.Hr, tenant, Workbook());

        Assert.Equal("hr.import.file_required", CodeOf(await ProblemAsync(none, HttpStatusCode.UnprocessableEntity)));
        Assert.Equal("hr.import.extension_invalid", CodeOf(await ProblemAsync(xls, HttpStatusCode.UnprocessableEntity)));
        Assert.Equal("hr.import.unreadable", CodeOf(await ProblemAsync(garbage, HttpStatusCode.UnprocessableEntity)));
        Assert.Equal("hr.import.header_mismatch", CodeOf(await ProblemAsync(headers, HttpStatusCode.UnprocessableEntity)));
        Assert.Equal("hr.import.empty", CodeOf(await ProblemAsync(empty, HttpStatusCode.UnprocessableEntity)));

        static byte[] EmployeeWorkbook()
        {
            using var workbook = new XLWorkbook();
            workbook.Worksheets.Add("Empleados").Cell(1, 1).Value = "NOMBRES";
            using var stream = new MemoryStream();
            workbook.SaveAs(stream);
            return stream.ToArray();
        }
    }

    [Fact]
    public async Task ImportWithoutMedicalManageReturns403()
    {
        var (tenant, _, _) = await SeedAsync();
        using var _ = tenant;
        using var reader = tenant.Client(HrPermissions.EmployeeRead, HrPermissions.MedicalRead, HrPermissions.ContractRead, HrPermissions.ContractManage);
        var file = Workbook(Row("9100000001"));

        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync($"{tenant.Url}/medical-exams/import/template", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PreviewAsync(reader, tenant, file)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ImportAsync(reader, tenant, file)).StatusCode);
    }
}
