using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modules.Hr.Application;
using Modules.Payroll.Application;
using Npgsql;
using static Modules.Payroll.IntegrationTests.PayrollApiHarness;
using static Modules.Payroll.IntegrationTests.PayrollApiSeeds;

namespace Modules.Payroll.IntegrationTests;

/// <summary>
/// El «Resumen de nómina» en Excel y la foto fija de los valores legales. Una empresa exonerada con un centro de clase I y un
/// empleado con el salario mínimo (el ancla <c>minimum-wage-first-half</c>: neto 929 801), más uno integral que no se liquida.
/// </summary>
public sealed class PayrollExportApiTests : IClassFixture<PayrollApiFixture>
{
    private const string ExcelContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    private const int TitleRow = 7;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly PayrollApiFixture _fixture;

    public PayrollExportApiTests(PayrollApiFixture fixture) => _fixture = fixture;

    internal sealed record Scene(PayrollTenant Tenant, SeededSetup Setup, Guid EmployeeId, Guid IntegralId, Guid PeriodId)
    {
        public string PeriodUrl => $"{Tenant.Url}/periods/{PeriodId}";
    }

    internal static async Task<Scene> NewSceneAsync(PayrollApiFixture fixture, string companyName = "Comercializadora QEP S.A.S.")
    {
        var tenant = await NewTenantAsync(fixture);
        var company = await SeedCompanyAsync(tenant, companyName, "Principal", "Bodega", riskClass: "I");
        var area = await CreatedAsync(tenant.Payroll, $"{tenant.HrBase}/areas", new { name = "Operaciones" });
        var position = await CreatedAsync(tenant.Payroll, $"{tenant.HrBase}/positions", new { name = "Analista" });
        var setup = new SeededSetup(
            company.Company, company.SiteId, company.WorkplaceId, area.GetProperty("id").GetGuid(), position.GetProperty("id").GetGuid());
        var employee = EmployeeIdOf(await HireAsync(tenant, HireBody(setup, startDate: "2025-01-01", amount: 1_750_905m)));
        var integral = EmployeeIdOf(await HireAsync(
            tenant,
            HireBody(setup, salaryType: "Integral", amount: 25_000_000m, documentNumber: "1002", firstName: "Beto", lastName: "Ramírez")));
        var ensured = await tenant.Payroll.PostAsync($"{tenant.Url}/companies/{setup.Company.Id}/periods/ensure-current", null, Ct);
        Assert.Equal(HttpStatusCode.OK, ensured.StatusCode);
        var periodId = (await JsonOfAsync(ensured)).GetProperty("items")[0].GetProperty("id").GetGuid();
        return new Scene(tenant, setup, employee, integral, periodId);
    }

    internal static async Task<string> VersionAsync(HttpClient client, string periodUrl)
    {
        var response = await client.GetAsync(periodUrl, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return $"\"{(await JsonOfAsync(response)).GetProperty("period").GetProperty("version").GetInt64()}\"";
    }

    internal static async Task<JsonElement> TransitionAsync(Scene scene, string action, object? body = null)
    {
        var response = await PostAsync(
            scene.Tenant.Payroll, $"{scene.PeriodUrl}/{action}", await VersionAsync(scene.Tenant.Payroll, scene.PeriodUrl), body);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await BodyAsync(response));
        return await JsonOfAsync(response);
    }

    private static Task<JsonElement> LiquidateAsync(Scene scene) => TransitionAsync(scene, "liquidate", new { confirmNoAbsences = true });

    private static async Task<HttpResponseMessage> ExportAsync(Scene scene, HttpClient? client = null) =>
        await (client ?? scene.Tenant.Payroll).GetAsync($"{scene.PeriodUrl}/export", Ct);

    private static async Task<XLWorkbook> WorkbookOfAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var bytes = await response.Content.ReadAsByteArrayAsync(Ct);
        return new XLWorkbook(new MemoryStream(bytes));
    }

    private static int ColumnOf(IXLWorksheet sheet, string title) =>
        Enumerable.Range(1, 31).First(column => sheet.Cell(TitleRow, column).GetString() == title);

    // Every cell from the titles down, joined: the «Generado» stamp lives above them and is not part of the comparison.
    private static string DumpOf(XLWorkbook workbook)
    {
        var sheet = workbook.Worksheet("Resumen");
        var text = new StringBuilder();
        for (var row = TitleRow; row <= sheet.LastRowUsed()!.RowNumber(); row++)
        {
            for (var column = 1; column <= 31; column++)
            {
                text.Append(sheet.Cell(row, column).GetString()).Append('|');
            }

            text.Append('\n');
        }

        return text.ToString();
    }

    [Fact]
    public async Task ADraftPeriodIsNotExportable()
    {
        var scene = await NewSceneAsync(_fixture);

        var response = await ExportAsync(scene);

        Assert.Equal((HttpStatusCode)422, response.StatusCode);
        Assert.Equal("payroll.period.not_exportable", CodeOf(await JsonOfAsync(response)));
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task TheFileHasTheHeaderTheRowsAndTheTotals()
    {
        var scene = await NewSceneAsync(_fixture);
        var detail = await LiquidateAsync(scene);

        var response = await ExportAsync(scene);
        using var workbook = await WorkbookOfAsync(response);

        Assert.Equal(ExcelContentType, response.Content.Headers.ContentType?.MediaType);
        var sheet = workbook.Worksheet("Resumen");
        Assert.Equal("Empresa", sheet.Cell(1, 1).GetString());
        Assert.Equal("Comercializadora QEP S.A.S.", sheet.Cell(1, 2).GetString());
        Assert.Equal(scene.Setup.Company.TaxId, sheet.Cell(1, 4).GetString());
        Assert.Equal("2026-11-01 a 2026-11-15", sheet.Cell(2, 2).GetString());
        Assert.Equal("Primera quincena", sheet.Cell(3, 2).GetString());
        Assert.Equal("Liquidado", sheet.Cell(4, 2).GetString());
        Assert.Matches(@"^2026-11-10 \d{2}:\d{2}$", sheet.Cell(5, 2).GetString());

        Assert.Equal("Tipo de documento", sheet.Cell(TitleRow, 1).GetString());
        Assert.Equal("Costo empresa", sheet.Cell(TitleRow, 31).GetString());
        var employeeRow = TitleRow + 1;
        Assert.Equal("1020304050", sheet.Cell(employeeRow, 2).GetString());
        Assert.Equal("Ana Pérez", sheet.Cell(employeeRow, 3).GetString());
        Assert.Equal("Analista", sheet.Cell(employeeRow, 4).GetString());
        Assert.Equal("Bodega - Principal", sheet.Cell(employeeRow, 5).GetString());
        Assert.Equal(15, sheet.Cell(employeeRow, 6).GetDouble());
        Assert.Equal(929_965, sheet.Cell(employeeRow, ColumnOf(sheet, "Neto")).GetDouble());
        Assert.Equal("#,##0", sheet.Cell(employeeRow, ColumnOf(sheet, "Neto")).Style.NumberFormat.Format);

        var totalsRow = employeeRow + 1;
        Assert.Equal("Totales", sheet.Cell(totalsRow, 1).GetString());
        var totals = detail.GetProperty("period").GetProperty("totals");
        Assert.Equal((double)totals.GetProperty("net").GetDecimal(), sheet.Cell(totalsRow, ColumnOf(sheet, "Neto")).GetDouble());
        Assert.Equal((double)totals.GetProperty("employerCost").GetDecimal(), sheet.Cell(totalsRow, ColumnOf(sheet, "Costo empresa")).GetDouble());
        Assert.False(sheet.Cell(totalsRow, ColumnOf(sheet, "Neto")).HasFormula);
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task TheExportShowsVacationDaysAndPay()
    {
        var scene = await NewSceneAsync(_fixture);
        var created = await scene.Tenant.Payroll.PostAsJsonAsync(
            $"{scene.Tenant.Url}/companies/{scene.Setup.Company.Id}/employees/{scene.EmployeeId}/vacations",
            new { origin = "Qep", startDate = "2026-11-09", endDate = "2026-11-13" },
            Ct);
        Assert.True(created.StatusCode == HttpStatusCode.Created, await BodyAsync(created));
        var detail = await LiquidateAsync(scene);
        var summary = (await JsonOfAsync(await scene.Tenant.Payroll.GetAsync($"{scene.PeriodUrl}/employees/{scene.EmployeeId}", Ct)))
            .GetProperty("result").GetProperty("summary");

        using var workbook = await WorkbookOfAsync(await ExportAsync(scene));

        // Five of the fifteen days are vacation: «Días» are the worked ones, and the vacation pay is what the engine stored.
        var sheet = workbook.Worksheet("Resumen");
        var employeeRow = TitleRow + 1;
        Assert.Equal(10, sheet.Cell(employeeRow, ColumnOf(sheet, "Días")).GetDouble());
        Assert.Equal(5, sheet.Cell(employeeRow, ColumnOf(sheet, "Días de vacaciones")).GetDouble());
        var pay = sheet.Cell(employeeRow, ColumnOf(sheet, "Vacaciones disfrutadas"));
        Assert.True(summary.GetProperty("vacationPay").GetDecimal() > 0m);
        Assert.Equal((double)summary.GetProperty("vacationPay").GetDecimal(), pay.GetDouble());
        Assert.Equal("#,##0", pay.Style.NumberFormat.Format);
        Assert.Equal(
            (double)detail.GetProperty("period").GetProperty("totals").GetProperty("vacationPay").GetDecimal(),
            sheet.Cell(employeeRow + 1, ColumnOf(sheet, "Vacaciones disfrutadas")).GetDouble());
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task TheExcludedGoToTheirSheet()
    {
        var scene = await NewSceneAsync(_fixture);
        await LiquidateAsync(scene);

        using var workbook = await WorkbookOfAsync(await ExportAsync(scene));

        var sheet = workbook.Worksheet("No liquidados");
        Assert.Equal("Motivo", sheet.Cell(1, 4).GetString());
        Assert.Equal("1002", sheet.Cell(2, 2).GetString());
        Assert.Equal("Beto Ramírez", sheet.Cell(2, 3).GetString());
        Assert.Equal("El tipo de salario no se liquida todavía", sheet.Cell(2, 4).GetString());
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task TheFileNameCarriesTheDates()
    {
        var scene = await NewSceneAsync(_fixture);
        await LiquidateAsync(scene);

        var response = await ExportAsync(scene);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var disposition = response.Content.Headers.ContentDisposition;
        Assert.Equal("resumen-nomina-2026-11-01-a-2026-11-15.xlsx", disposition?.FileNameStar ?? disposition?.FileName);
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task AnApprovedPeriodSaysSo()
    {
        var scene = await NewSceneAsync(_fixture);
        await LiquidateAsync(scene);
        await TransitionAsync(scene, "approve");

        using var workbook = await WorkbookOfAsync(await ExportAsync(scene));

        Assert.Equal("Aprobado", workbook.Worksheet("Resumen").Cell(4, 2).GetString());
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task AnOutdatedPeriodSaysSo()
    {
        var scene = await NewSceneAsync(_fixture);
        await LiquidateAsync(scene);
        var captured = await PutAsync(
            scene.Tenant.Payroll,
            $"{scene.PeriodUrl}/employees/{scene.EmployeeId}/inputs",
            new
            {
                overtime = Array.Empty<object>(),
                incomes = new[] { new { conceptId = Guid.Parse("00000000-0000-0000-0002-000000000001"), amount = 100_000m, salaryConstitutive = true } },
                deductions = Array.Empty<object>(),
            },
            await VersionAsync(scene.Tenant.Payroll, scene.PeriodUrl));
        Assert.Equal(HttpStatusCode.OK, captured.StatusCode);

        using var workbook = await WorkbookOfAsync(await ExportAsync(scene));

        Assert.Equal("Liquidado (desactualizado)", workbook.Worksheet("Resumen").Cell(4, 2).GetString());
        scene.Tenant.Dispose();
    }

    [Fact]
    public async Task APeriodWhoseSourceChangedSaysSo()
    {
        var scene = await NewSceneAsync(_fixture);
        await LiquidateAsync(scene);
        var employee = await JsonOfAsync(await scene.Tenant.Payroll.GetAsync($"{scene.Tenant.HrBase}/employees/{scene.EmployeeId}", Ct));
        var contractId = employee.GetProperty("contract").GetProperty("id").GetGuid();
        var raise = await scene.Tenant.Payroll.PostAsJsonAsync(
            $"{scene.Tenant.HrBase}/employees/{scene.EmployeeId}/contracts/{contractId}/salaries",
            new { effectiveDate = "2026-11-05", amount = 2_000_000m },
            Ct);
        Assert.True(raise.StatusCode == HttpStatusCode.OK, await BodyAsync(raise));

        using var workbook = await WorkbookOfAsync(await ExportAsync(scene));

        Assert.Equal("Liquidado (desactualizado)", workbook.Worksheet("Resumen").Cell(4, 2).GetString());
        scene.Tenant.Dispose();
    }
    [Fact]
    public async Task ExportNeedsPeriodRead()
    {
        var scene = await NewSceneAsync(_fixture);
        await LiquidateAsync(scene);
        using var manager = scene.Tenant.Client(PayrollPermissions.PeriodManage, PayrollPermissions.SettingsManage);

        var response = await ExportAsync(scene, manager);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        scene.Tenant.Dispose();
    }

    // The table is global to the class's database: the row is removed in `finally` so later tests do not read the new value.
    private static async Task ExecuteAsync(PayrollApiFixture fixture, string sql)
    {
        var connectionString = fixture.Factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("QepDatabase")!;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
        // La caché del pod (área apagada, 60 s) no ve el SQL a mano: sin esto la fila nueva queda invisible y la prueba pasa en vacío.
        await fixture.Factory.Services.GetRequiredService<ILegalParameterCacheVersion>().BumpAsync(Ct);
    }

    private const string InsertNewTransportAllowance =
        "INSERT INTO hr.legal_parameters (key, effective_from, value) VALUES ('TransportAllowance', '2026-11-01', 300000)";

    private const string DeleteNewTransportAllowance =
        "DELETE FROM hr.legal_parameters WHERE key = 'TransportAllowance' AND effective_from = '2026-11-01'";

    // Review Focus 3: a new legal value changes neither a closed period nor a reopened one that is liquidated again.
    [Fact]
    public async Task ANewLegalValueChangesNeitherAClosedNorAReliquidatedPeriod()
    {
        var scene = await NewSceneAsync(_fixture);
        await LiquidateAsync(scene);
        await TransitionAsync(scene, "approve");
        await TransitionAsync(scene, "close");

        var detailBefore = await JsonOfAsync(await scene.Tenant.Payroll.GetAsync(scene.PeriodUrl, Ct));
        var employeeBefore = await JsonOfAsync(await scene.Tenant.Payroll.GetAsync($"{scene.PeriodUrl}/employees/{scene.EmployeeId}", Ct));
        string excelBefore;
        using (var workbook = await WorkbookOfAsync(await ExportAsync(scene)))
        {
            excelBefore = DumpOf(workbook);
        }

        await ExecuteAsync(_fixture, InsertNewTransportAllowance);
        try
        {
            await AssertNothingChangedAsync(scene, detailBefore, employeeBefore, excelBefore);
            await TransitionAsync(scene, "reopen", new { reason = "Corregir" });
            await LiquidateAsync(scene);
        }
        finally
        {
            await ExecuteAsync(_fixture, DeleteNewTransportAllowance);
        }

        var again = await JsonOfAsync(await scene.Tenant.Payroll.GetAsync($"{scene.PeriodUrl}/employees/{scene.EmployeeId}", Ct));
        var transport = again.GetProperty("result").GetProperty("lines").EnumerateArray()
            .Single(line => line.GetProperty("code").GetString() == "transport_allowance");
        Assert.Equal(124_548m, transport.GetProperty("amount").GetDecimal());
        Assert.Equal(
            employeeBefore.GetProperty("result").GetProperty("legalValues").GetRawText(),
            again.GetProperty("result").GetProperty("legalValues").GetRawText());
        scene.Tenant.Dispose();
    }

    private static async Task AssertNothingChangedAsync(Scene scene, JsonElement detailBefore, JsonElement employeeBefore, string excelBefore)
    {
        var detailAfter = await JsonOfAsync(await scene.Tenant.Payroll.GetAsync(scene.PeriodUrl, Ct));
        var employeeAfter = await JsonOfAsync(await scene.Tenant.Payroll.GetAsync($"{scene.PeriodUrl}/employees/{scene.EmployeeId}", Ct));
        using (var workbook = await WorkbookOfAsync(await ExportAsync(scene)))
        {
            Assert.Equal(excelBefore, DumpOf(workbook));
        }

        Assert.Equal(detailBefore.GetProperty("employees").GetRawText(), detailAfter.GetProperty("employees").GetRawText());
        Assert.Equal(detailBefore.GetProperty("period").GetProperty("totals").GetRawText(), detailAfter.GetProperty("period").GetProperty("totals").GetRawText());
        Assert.Equal(employeeBefore.GetProperty("result").GetRawText(), employeeAfter.GetProperty("result").GetRawText());
    }

    // Review M-1: text that starts like a formula is stored and shown as text, never evaluated.
    [Fact]
    public async Task TextStartingLikeAFormulaIsStoredAsText()
    {
        var tenant = await NewTenantAsync(_fixture);
        var company = await SeedCompanyAsync(tenant, "=Empresa Peligrosa", "Principal", "+Bodega", riskClass: "I");
        var area = await CreatedAsync(tenant.Payroll, $"{tenant.HrBase}/areas", new { name = "Operaciones" });
        var position = await CreatedAsync(tenant.Payroll, $"{tenant.HrBase}/positions", new { name = "@Analista" });
        var setup = new SeededSetup(
            company.Company, company.SiteId, company.WorkplaceId, area.GetProperty("id").GetGuid(), position.GetProperty("id").GetGuid());
        await HireAsync(tenant, HireBody(setup, startDate: "2025-01-01", amount: 1_750_905m, firstName: "=SUMA(1;1)", lastName: "-Ruiz"));
        var ensured = await tenant.Payroll.PostAsync($"{tenant.Url}/companies/{setup.Company.Id}/periods/ensure-current", null, Ct);
        var periodId = (await JsonOfAsync(ensured)).GetProperty("items")[0].GetProperty("id").GetGuid();
        var scene = new Scene(tenant, setup, Guid.Empty, Guid.Empty, periodId);
        await LiquidateAsync(scene);

        using var workbook = await WorkbookOfAsync(await ExportAsync(scene));

        var sheet = workbook.Worksheet("Resumen");
        var name = sheet.Cell(TitleRow + 1, 3);
        Assert.False(name.HasFormula);
        Assert.Equal("=SUMA(1;1) -Ruiz", name.GetString());
        Assert.False(sheet.Cell(1, 2).HasFormula);
        Assert.Equal("=Empresa Peligrosa", sheet.Cell(1, 2).GetString());
        Assert.False(sheet.Cell(TitleRow + 1, 4).HasFormula);
        Assert.Equal("@Analista", sheet.Cell(TitleRow + 1, 4).GetString());
        Assert.False(sheet.Cell(TitleRow + 1, 5).HasFormula);
        Assert.Equal("+Bodega - Principal", sheet.Cell(TitleRow + 1, 5).GetString());
        tenant.Dispose();
    }
}
