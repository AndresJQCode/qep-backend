using System.Net;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.Extensions.DependencyInjection;
using Modules.Payroll.Application;
using static Modules.Payroll.IntegrationTests.PayrollApiHarness;
using static Modules.Payroll.IntegrationTests.PayrollApiSeeds;

namespace Modules.Payroll.IntegrationTests;

/// <summary>
/// La importación masiva de novedades: plantilla, vista previa (no escribe) y confirmación (todo o nada, reemplaza lo de cada
/// empleado del archivo). Los archivos se arman con ClosedXML, como en la importación de empleados de Hr.
/// </summary>
public sealed class PayrollInputsImportApiTests : IClassFixture<PayrollApiFixture>
{
    private const string ExcelContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly PayrollApiFixture _fixture;

    public PayrollInputsImportApiTests(PayrollApiFixture fixture) => _fixture = fixture;

    private sealed record Scene(PayrollTenant Tenant, SeededSetup Setup, Guid PeriodId, Guid AnaId, Guid BetoId)
    {
        public string PeriodUrl => $"{Tenant.Url}/periods/{PeriodId}";
    }

    // Ana (1001) y Beto (1002), dos empleados estándar, y la primera quincena de noviembre abierta.
    private async Task<Scene> NewSceneAsync()
    {
        var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var ana = EmployeeIdOf(await HireAsync(tenant, HireBody(setup, documentNumber: "1001", firstName: "Ana", lastName: "Pérez")));
        var beto = EmployeeIdOf(await HireAsync(tenant, HireBody(setup, documentNumber: "1002", firstName: "Beto", lastName: "Ramírez")));
        var ensured = await tenant.Payroll.PostAsync($"{tenant.Url}/companies/{setup.Company.Id}/periods/ensure-current", null, Ct);
        Assert.Equal(HttpStatusCode.OK, ensured.StatusCode);
        var periodId = (await JsonOfAsync(ensured)).GetProperty("items")[0].GetProperty("id").GetGuid();
        return new Scene(tenant, setup, periodId, ana, beto);
    }

    private static Dictionary<string, string?> Row(
        string document, string concept, string? hours = null, string? amount = null, string? constitutive = null) => new()
        {
            [InputsImportColumns.DocumentNumber] = document,
            [InputsImportColumns.Concept] = concept,
            [InputsImportColumns.Hours] = hours,
            [InputsImportColumns.Amount] = amount,
            [InputsImportColumns.SalaryConstitutive] = constitutive,
        };

    private static byte[] Workbook(params Dictionary<string, string?>[] rows) => Workbook(InputsImportColumns.Ordered, rows);

    private static byte[] Workbook(IReadOnlyList<string> headers, IEnumerable<Dictionary<string, string?>> rows)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add(InputsImportColumns.DataSheetName);
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

    private static MultipartFormDataContent Upload(byte[] content, string fileName = "novedades.xlsx")
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new(ExcelContentType);
        form.Add(file, "file", fileName);
        return form;
    }

    private static async Task<HttpResponseMessage> PreviewAsync(Scene scene, byte[] content, string fileName = "novedades.xlsx") =>
        await scene.Tenant.Payroll.PostAsync($"{scene.PeriodUrl}/inputs/import/preview", Upload(content, fileName), Ct);

    private static async Task<HttpResponseMessage> ConfirmAsync(
        Scene scene, byte[] content, string? ifMatch, string fileName = "novedades.xlsx", HttpClient? client = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{scene.PeriodUrl}/inputs/import") { Content = Upload(content, fileName) };
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return await (client ?? scene.Tenant.Payroll).SendAsync(request, Ct);
    }

    private static async Task<string> VersionAsync(Scene scene)
    {
        var response = await scene.Tenant.Payroll.GetAsync(scene.PeriodUrl, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return $"\"{(await JsonOfAsync(response)).GetProperty("period").GetProperty("version").GetInt64()}\"";
    }

    private static async Task<HttpResponseMessage> ConfirmCurrentAsync(Scene scene, byte[] content, string fileName = "novedades.xlsx") =>
        await ConfirmAsync(scene, content, await VersionAsync(scene), fileName);

    private static async Task<JsonElement> OkAsync(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode == HttpStatusCode.OK, await BodyAsync(response));
        return await JsonOfAsync(response);
    }

    private static async Task<JsonElement> EmployeeAsync(Scene scene, Guid employeeId) =>
        await OkAsync(await scene.Tenant.Payroll.GetAsync($"{scene.PeriodUrl}/employees/{employeeId}", Ct));

    private static async Task<JsonElement> CaptureAsync(Scene scene, Guid employeeId, object body)
    {
        var response = await PutAsync(
            scene.Tenant.Payroll, $"{scene.PeriodUrl}/employees/{employeeId}/inputs", body, await VersionAsync(scene));
        return await OkAsync(response);
    }

    [Fact]
    public async Task TheTemplateHasTheThreeSheetsAndTheConcepts()
    {
        var scene = await NewSceneAsync();
        await HireAsync(
            scene.Tenant,
            HireBody(scene.Setup, salaryType: "Integral", amount: 25_000_000m, documentNumber: "1003", firstName: "Carla", lastName: "Soto"));
        var own = await CreatedAsync(
            scene.Tenant.Payroll, $"{scene.Tenant.Url}/companies/{scene.Setup.Company.Id}/concepts",
            new { kind = "Deduction", name = "Libranza", defaultSalaryConstitutive = (bool?)null, multiplier = (decimal?)null });
        var archived = await CreatedAsync(
            scene.Tenant.Payroll, $"{scene.Tenant.Url}/companies/{scene.Setup.Company.Id}/concepts",
            new { kind = "Deduction", name = "Embargo viejo", defaultSalaryConstitutive = (bool?)null, multiplier = (decimal?)null });
        await scene.Tenant.Payroll.PostAsync(
            $"{scene.Tenant.Url}/companies/{scene.Setup.Company.Id}/concepts/{archived.GetProperty("id").GetGuid()}/archive", null, Ct);
        Assert.NotEqual(Guid.Empty, own.GetProperty("id").GetGuid());

        var response = await scene.Tenant.Payroll.GetAsync($"{scene.PeriodUrl}/inputs/template", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ExcelContentType, response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("plantilla-novedades.xlsx", response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName);
        await using var stream = await response.Content.ReadAsStreamAsync(Ct);
        using var workbook = new XLWorkbook(stream);
        Assert.Equal(
            ["Novedades", "Empleados", "Valores válidos"], workbook.Worksheets.Select(sheet => sheet.Name).ToArray());

        var data = workbook.Worksheet("Novedades");
        Assert.Equal(InputsImportColumns.Ordered, Enumerable.Range(1, 6).Select(column => data.Cell(1, column).GetString()).ToArray());

        var employees = workbook.Worksheet("Empleados");
        var documents = employees.Column(1).CellsUsed().Skip(1).Select(cell => cell.GetString()).ToList();
        Assert.Equal(["1001", "1002"], documents.Order());

        var values = workbook.Worksheet("Valores válidos").Column(1).CellsUsed().Skip(1).Select(cell => cell.GetString()).ToList();
        Assert.Equal(13, values.Count);
        Assert.Contains("Hora extra diurna", values);
        Assert.Contains("Otra deducción", values);
        Assert.Contains("Libranza", values);
        Assert.DoesNotContain("Embargo viejo", values);
        Assert.Contains(PayrollLabels.Yes, workbook.Worksheet("Valores válidos").Column(2).CellsUsed().Select(cell => cell.GetString()));
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task PreviewWritesNothing()
    {
        var scene = await NewSceneAsync();
        var before = await VersionAsync(scene);

        var preview = await OkAsync(await PreviewAsync(scene, Workbook(
            Row("1001", "Hora extra diurna", hours: "4.5"),
            Row("1001", "Comisiones", amount: "500000"),
            Row("1002", "Otra deducción", amount: "20000"))));

        Assert.Equal(3, preview.GetProperty("totalRows").GetInt32());
        Assert.Equal(3, preview.GetProperty("validRows").GetArrayLength());
        Assert.Equal(0, preview.GetProperty("errors").GetArrayLength());
        var summary = preview.GetProperty("summary");
        Assert.Equal(2, summary.GetProperty("employees").GetInt32());
        Assert.Equal(1, summary.GetProperty("overtimeLines").GetInt32());
        Assert.Equal(1, summary.GetProperty("incomeLines").GetInt32());
        Assert.Equal(1, summary.GetProperty("deductionLines").GetInt32());
        var commissions = preview.GetProperty("validRows")[1];
        Assert.Equal("Comisiones", commissions.GetProperty("concept").GetString());
        Assert.Equal("Income", commissions.GetProperty("kind").GetString());
        Assert.True(commissions.GetProperty("salaryConstitutive").GetBoolean());
        Assert.Equal("1001", commissions.GetProperty("documentNumber").GetString());

        Assert.Equal(before, await VersionAsync(scene));
        Assert.Equal(0, (await EmployeeAsync(scene, scene.AnaId)).GetProperty("inputs").GetProperty("overtime").GetArrayLength());
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task ImportReplacesOnlyTheEmployeesInTheFile()
    {
        var scene = await NewSceneAsync();
        var commissions = Guid.Parse("00000000-0000-0000-0002-000000000001");
        var otherDeduction = Guid.Parse("00000000-0000-0000-0003-000000000001");
        await CaptureAsync(scene, scene.AnaId, new
        {
            overtime = Array.Empty<object>(),
            incomes = new[] { new { conceptId = commissions, amount = 111_000m, salaryConstitutive = true } },
            deductions = Array.Empty<object>(),
        });
        await CaptureAsync(scene, scene.BetoId, new
        {
            overtime = Array.Empty<object>(),
            incomes = Array.Empty<object>(),
            deductions = new[] { new { conceptId = otherDeduction, amount = 9_000m } },
        });

        var detail = await OkAsync(await ConfirmCurrentAsync(scene, Workbook(Row("1001", "Hora extra diurna", hours: "3"))));

        var ana = await EmployeeAsync(scene, scene.AnaId);
        Assert.Equal(1, ana.GetProperty("inputs").GetProperty("overtime").GetArrayLength());
        Assert.Equal(0, ana.GetProperty("inputs").GetProperty("incomes").GetArrayLength());
        var beto = await EmployeeAsync(scene, scene.BetoId);
        Assert.Equal(9_000m, Assert.Single(beto.GetProperty("inputs").GetProperty("deductions").EnumerateArray()).GetProperty("amount").GetDecimal());
        Assert.Equal("Draft", detail.GetProperty("period").GetProperty("status").GetString());
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task ImportingOnALiquidatedPeriodOutdatesIt()
    {
        var scene = await NewSceneAsync();
        var liquidate = await PostAsync(scene.Tenant.Payroll, $"{scene.PeriodUrl}/liquidate", await VersionAsync(scene), new { confirmNoAbsences = true });
        Assert.Equal(HttpStatusCode.OK, liquidate.StatusCode);

        var detail = await OkAsync(await ConfirmCurrentAsync(scene, Workbook(Row("1001", "Comisiones", amount: "250000"))));

        Assert.True(detail.GetProperty("period").GetProperty("outdated").GetBoolean());
        // Both events share one instant: the history keeps the order they were written in (review M-3).
        Assert.Equal(
            ["MarkedOutdated", "InputsImported", "Liquidated", "Created"],
            detail.GetProperty("history").EnumerateArray().Select(item => item.GetProperty("action").GetString()));
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task ConfirmingWithErrorsIs422AndWritesNothing()
    {
        var scene = await NewSceneAsync();
        var before = await VersionAsync(scene);

        var response = await ConfirmAsync(scene, Workbook(
            Row("1001", "Hora extra diurna", hours: "2"),
            Row("1002", "Concepto fantasma", amount: "100")), before);

        Assert.Equal((HttpStatusCode)422, response.StatusCode);
        Assert.Equal("payroll.import.rows_invalid", CodeOf(await JsonOfAsync(response)));
        Assert.Equal(before, await VersionAsync(scene));
        Assert.Equal(0, (await EmployeeAsync(scene, scene.AnaId)).GetProperty("inputs").GetProperty("overtime").GetArrayLength());

        var preview = await OkAsync(await PreviewAsync(scene, Workbook(
            Row("1001", "Hora extra diurna", hours: "2"),
            Row("1002", "Concepto fantasma", amount: "100"))));
        var error = Assert.Single(preview.GetProperty("errors").EnumerateArray());
        Assert.Equal(3, error.GetProperty("rowNumber").GetInt32());
        Assert.Equal("payroll.import.row.concept_not_found", error.GetProperty("code").GetString());
        Assert.Equal("concept", error.GetProperty("field").GetString());
        Assert.Equal(1, preview.GetProperty("validRows").GetArrayLength());
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task ARepeatedOvertimeMarksBothRows()
    {
        var scene = await NewSceneAsync();

        var preview = await OkAsync(await PreviewAsync(scene, Workbook(
            Row("1001", "Hora extra diurna", hours: "2"),
            Row("1001", "hora extra diurna", hours: "3"),
            Row("1002", "Hora extra diurna", hours: "1"))));

        var errors = preview.GetProperty("errors").EnumerateArray().ToList();
        Assert.Equal([2, 3], errors.Select(error => error.GetProperty("rowNumber").GetInt32()));
        Assert.All(errors, error => Assert.Equal("payroll.import.row.overtime_duplicated_in_file", error.GetProperty("code").GetString()));
        Assert.Equal(1, preview.GetProperty("validRows").GetArrayLength());
        scene.Tenant.Dispose();
    }

    // Pre-flight M-13: the same limits as a manual capture.
    [Fact]
    public async Task TheImportAppliesTheCaptureLimits()
    {
        var scene = await NewSceneAsync();
        var rows = Enumerable.Range(0, 51).Select(index => Row("1001", "Comisiones", amount: $"{1000 + index}")).ToArray();
        var before = await VersionAsync(scene);

        var preview = await OkAsync(await PreviewAsync(scene, Workbook(rows)));

        var error = Assert.Single(preview.GetProperty("errors").EnumerateArray());
        Assert.Equal(52, error.GetProperty("rowNumber").GetInt32());
        Assert.Equal("payroll.import.row.too_many_lines", error.GetProperty("code").GetString());
        Assert.Equal(50, preview.GetProperty("validRows").GetArrayLength());

        var response = await ConfirmAsync(scene, Workbook(rows), before);
        Assert.Equal((HttpStatusCode)422, response.StatusCode);
        Assert.Equal("payroll.import.rows_invalid", CodeOf(await JsonOfAsync(response)));
        Assert.Equal(0, (await EmployeeAsync(scene, scene.AnaId)).GetProperty("inputs").GetProperty("incomes").GetArrayLength());
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task ImportNeedsIfMatch()
    {
        var scene = await NewSceneAsync();
        var file = Workbook(Row("1001", "Hora extra diurna", hours: "2"));

        var missing = await ConfirmAsync(scene, file, null);
        var stale = await ConfirmAsync(scene, file, "\"999\"");

        Assert.Equal((HttpStatusCode)428, missing.StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal(0, (await EmployeeAsync(scene, scene.AnaId)).GetProperty("inputs").GetProperty("overtime").GetArrayLength());
        scene.Tenant.Dispose();
    }

    [Theory]
    [InlineData("NoFile", "payroll.import.file_required")]
    [InlineData("Xls", "payroll.import.extension_invalid")]
    [InlineData("TooLarge", "payroll.import.too_large")]
    [InlineData("Garbage", "payroll.import.unreadable")]
    [InlineData("OtherHeaders", "payroll.import.header_mismatch")]
    [InlineData("OnlyHeaders", "payroll.import.empty")]
    [InlineData("TooManyRows", "payroll.import.too_many_rows")]
    public async Task FileErrors(string scenario, string code)
    {
        var scene = await NewSceneAsync();

        var response = scenario switch
        {
            "NoFile" => await scene.Tenant.Payroll.PostAsync(
                $"{scene.PeriodUrl}/inputs/import/preview", new MultipartFormDataContent { { new StringContent("x"), "other" } }, Ct),
            "Xls" => await PreviewAsync(scene, Workbook(Row("1001", "Comisiones", amount: "1")), "novedades.xls"),
            "TooLarge" => await PreviewAsync(scene, new byte[(5 * 1024 * 1024) + 1]),
            "Garbage" => await PreviewAsync(scene, [1, 2, 3, 4, 5]),
            "OtherHeaders" => await PreviewAsync(scene, Workbook(["A", "B"], [new Dictionary<string, string?> { ["A"] = "x" }])),
            "OnlyHeaders" => await PreviewAsync(scene, Workbook()),
            "TooManyRows" => await PreviewAsync(scene, Workbook(Enumerable.Range(0, 5001).Select(_ => Row("1001", "Comisiones", amount: "1")).ToArray())),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };

        Assert.Equal((HttpStatusCode)422, response.StatusCode);
        Assert.Equal(code, CodeOf(await JsonOfAsync(response)));
        scene.Tenant.Dispose();
    }

    // The 5 MB check in the analyzer runs after the form is buffered, so the routes carry a body limit of their own (Kestrel turns the
    // metadata into the real limit; the in-memory test server does not enforce it, so the metadata is what is asserted).
    [Fact]
    public void TheImportRoutesCarryARequestSizeLimit()
    {
        var limits = _fixture.Factory.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>().Endpoints
            .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.Contains("/inputs/import", StringComparison.Ordinal) == true
                && endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods.Contains("POST") == true)
            .Select(endpoint => endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Http.Metadata.IRequestSizeLimitMetadata>())
            .ToList();

        Assert.Equal(2, limits.Count);
        Assert.All(limits, limit =>
        {
            Assert.NotNull(limit);
            Assert.True(limit.MaxRequestBodySize > InputsImportColumns.MaxSizeInBytes);
            Assert.True(limit.MaxRequestBodySize <= 2 * InputsImportColumns.MaxSizeInBytes);
        });
    }
    [Fact]
    public async Task ApprovedPeriodsDoNotImport()
    {
        var scene = await NewSceneAsync();
        Assert.Equal(
            HttpStatusCode.OK,
            (await PostAsync(scene.Tenant.Payroll, $"{scene.PeriodUrl}/liquidate", await VersionAsync(scene), new { confirmNoAbsences = true })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(scene.Tenant.Payroll, $"{scene.PeriodUrl}/approve", await VersionAsync(scene))).StatusCode);
        var file = Workbook(Row("1001", "Hora extra diurna", hours: "2"));

        var confirm = await ConfirmCurrentAsync(scene, file);
        var preview = await PreviewAsync(scene, file);
        var template = await scene.Tenant.Payroll.GetAsync($"{scene.PeriodUrl}/inputs/template", Ct);

        foreach (var response in new[] { confirm, preview, template })
        {
            Assert.Equal((HttpStatusCode)422, response.StatusCode);
            Assert.Equal("payroll.period.not_editable", CodeOf(await JsonOfAsync(response)));
        }

        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task ImportIsAuditedAndLeavesAnEvent()
    {
        var scene = await NewSceneAsync();

        var detail = await OkAsync(await ConfirmCurrentAsync(scene, Workbook(Row("1001", "Hora extra diurna", hours: "2"))));

        Assert.Equal(1, await CountAuditEntriesAsync(_fixture.Factory, PayrollAuditActions.InputsImported, scene.PeriodId.ToString()));
        Assert.Equal("InputsImported", detail.GetProperty("history")[0].GetProperty("action").GetString());
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task ImportNeedsPeriodManage()
    {
        var scene = await NewSceneAsync();
        using var reader = scene.Tenant.Client(PayrollPermissions.PeriodRead);

        var response = await ConfirmAsync(scene, Workbook(Row("1001", "Hora extra diurna", hours: "2")), "\"1\"", client: reader);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        scene.Tenant.Dispose();
    }
}
