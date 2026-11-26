using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Hr.Application;
using Modules.Tenancy.Application;
using Testcontainers.PostgreSql;
using static Modules.Hr.IntegrationTests.HrApiHarness;

namespace Modules.Hr.IntegrationTests;

/// <summary>
/// La carga anual de valores legales por Excel de punta a punta (spec 2026-11-22, «Carga por Excel»). El archivo sale de la plantilla
/// que descarga el propio endpoint y se edita con ClosedXML. El tenant de pruebas hace de operador, el uso por nóminas calculadas es un
/// doble vacío y el contador de versión de la caché es un doble que cuenta.
/// </summary>
public sealed class LegalParameterImportApiTests
{
    private const string ExcelContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private static readonly DateTimeOffset Today = new(2026, 11, 30, 15, 0, 0, TimeSpan.Zero);

    private static readonly Guid OperatorTenant = Guid.Parse(TenantId);

    private static readonly string[] ReadAndManage =
        [HrOperatorPermissions.LegalParametersRead, HrOperatorPermissions.LegalParametersManage];

    private static string Base() => $"/api/v1/tenants/{TenantId}/operator/legal-parameters";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class CountingCacheVersion : ILegalParameterCacheVersion
    {
        public int Bumps { get; private set; }

        public Task BumpAsync(CancellationToken cancellationToken)
        {
            Bumps++;
            return Task.CompletedTask;
        }
    }

    private sealed class EmptyUsage : ILegalParameterUsage
    {
        public Task<IReadOnlyList<Modules.Hr.Domain.CalculatedPeriodStamp>> ListCalculatedPeriodsOfAllTenantsAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Modules.Hr.Domain.CalculatedPeriodStamp>>([]);
    }

    private sealed record World(
        PostgreSqlContainer Database, QepApiFactory Factory, HttpClient Client, CountingCacheVersion Versions)
        : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            Factory.Dispose();
            await Database.DisposeAsync();
        }
    }

    private static async Task<World> OpenAsync(params string[] permissions)
    {
        var database = await StartDatabaseAsync();
        var clock = new MutableTenantClock(Today);
        var versions = new CountingCacheVersion();
        var factory = new QepApiFactory(
            database.GetConnectionString(),
            services =>
            {
                services.RemoveAll<ITenantClock>();
                services.AddSingleton<ITenantClock>(clock);
                services.RemoveAll<ILegalParameterUsage>();
                services.AddSingleton<ILegalParameterUsage>(new EmptyUsage());
                services.RemoveAll<ILegalParameterCacheVersion>();
                services.AddSingleton<ILegalParameterCacheVersion>(versions);
            },
            OperatorTenant);
        await SeedTenantAsync(factory);
        var client = CreateClient(factory, SubjectId, TenantId, permissions.Length == 0 ? ReadAndManage : permissions);
        return new World(database, factory, client, versions);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement;
    }

    private static async Task ProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == status, $"{(int)response.StatusCode}: {body}");
        Assert.Equal(code, JsonDocument.Parse(body).RootElement.GetProperty("code").GetString());
    }

    private static async Task<byte[]> DownloadTemplateAsync(World world)
    {
        var response = await world.Client.GetAsync(Base() + "/import/template", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsByteArrayAsync(Ct);
    }

    // Cambia «Valor nuevo» de una clave de la plantilla descargada.
    private static byte[] Edit(byte[] template, string key, double value)
    {
        using var workbook = new XLWorkbook(new MemoryStream(template));
        var sheet = workbook.Worksheet(LegalParameterImportColumns.ParametersSheet);
        var row = sheet.RowsUsed().Skip(1).Single(item => item.Cell(1).GetString() == key);
        row.Cell(6).Value = value;
        return Save(workbook);
    }

    private static byte[] Save(XLWorkbook workbook)
    {
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static MultipartFormDataContent Upload(byte[] content, string fileName = "parametros-2027.xlsx")
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new(ExcelContentType);
        form.Add(file, "file", fileName);
        return form;
    }

    private static Task<HttpResponseMessage> PreviewAsync(World world, byte[] content) =>
        world.Client.PostAsync(Base() + "/import/preview", Upload(content), Ct);

    private static Task<HttpResponseMessage> ImportAsync(World world, byte[] content, string fileName = "parametros-2027.xlsx") =>
        world.Client.PostAsync(Base() + "/import", Upload(content, fileName), Ct);

    private static async Task<int> HistoryTotalAsync(World world) =>
        (await JsonAsync(await world.Client.GetAsync(Base() + "/history", Ct))).GetProperty("total").GetInt32();

    [Fact]
    public async Task OnlyManageCanTouchTheImport()
    {
        await using var world = await OpenAsync(HrOperatorPermissions.LegalParametersRead);

        var template = await world.Client.GetAsync(Base() + "/import/template", Ct);
        var preview = await world.Client.PostAsync(Base() + "/import/preview", Upload([1, 2, 3]), Ct);
        var import = await world.Client.PostAsync(Base() + "/import", Upload([1, 2, 3]), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, template.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, preview.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, import.StatusCode);
    }

    [Fact]
    public async Task TheTemplateDownloadsAsXlsx()
    {
        await using var world = await OpenAsync();

        var response = await world.Client.GetAsync(Base() + "/import/template", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ExcelContentType, response.Content.Headers.ContentType?.MediaType);
        var disposition = response.Content.Headers.ContentDisposition;
        Assert.Equal("parametros-legales-2027.xlsx", disposition?.FileNameStar ?? disposition?.FileName?.Trim('"'));
        using var workbook = new XLWorkbook(new MemoryStream(await response.Content.ReadAsByteArrayAsync(Ct)));
        Assert.Equal(30, workbook.Worksheet(LegalParameterImportColumns.ParametersSheet).RowsUsed().Count() - 1);
        Assert.Equal(6, workbook.Worksheet(LegalParameterImportColumns.BracketsSheet).RowsUsed().Count() - 1);
    }

    [Fact]
    public async Task PreviewWritesNothing()
    {
        await using var world = await OpenAsync();
        var before = await JsonAsync(await world.Client.GetAsync(Base() + "/", Ct));
        var file = Edit(await DownloadTemplateAsync(world), "Smmlv", 2_000_000);

        var preview = await JsonAsync(await PreviewAsync(world, file));

        Assert.True(preview.GetProperty("canApply").GetBoolean());
        var summary = preview.GetProperty("summary");
        Assert.Equal(1, summary.GetProperty("create").GetInt32());
        Assert.Equal(27, summary.GetProperty("carry").GetInt32());
        Assert.Equal(0, summary.GetProperty("error").GetInt32());
        Assert.Empty(preview.GetProperty("warnings").EnumerateArray());
        var smmlv = preview.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("key").GetString() == "Smmlv");
        Assert.Equal("Create", smmlv.GetProperty("action").GetString());
        Assert.Equal(2_000_000m, smmlv.GetProperty("newValue").GetDecimal());
        Assert.Equal(0, await HistoryTotalAsync(world));
        Assert.Equal(0, world.Versions.Bumps);
        var after = await JsonAsync(await world.Client.GetAsync(Base() + "/", Ct));
        Assert.Equal(before.GetRawText(), after.GetRawText());
    }

    [Fact]
    public async Task OneErrorAppliesNothing()
    {
        await using var world = await OpenAsync();
        var file = Edit(Edit(await DownloadTemplateAsync(world), "Smmlv", 2_000_000), "HealthEmployeeRate", 12);

        var preview = await JsonAsync(await PreviewAsync(world, file));
        var response = await ImportAsync(world, file);

        Assert.False(preview.GetProperty("canApply").GetBoolean());
        var bad = preview.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("action").GetString() == "Error");
        Assert.Equal("hr.legal_parameter.import.row.value_invalid", bad.GetProperty("code").GetString());
        Assert.Equal(
            "La tasa tiene que estar entre 0 % y 100 %. ¿Escribiste 12 en una celda sin formato de porcentaje?",
            bad.GetProperty("message").GetString());
        await ProblemAsync(response, HttpStatusCode.UnprocessableEntity, "hr.legal_parameter.import.rows_invalid");
        Assert.Equal(0, await HistoryTotalAsync(world));
        Assert.Equal(0, world.Versions.Bumps);
    }

    [Fact]
    public async Task TheImportSharesOneBatchAndBumpsOnce()
    {
        await using var world = await OpenAsync();
        var file = Edit(await DownloadTemplateAsync(world), "Smmlv", 2_000_000);

        var result = await JsonAsync(await ImportAsync(world, file, "carga-2027.xlsx"));

        Assert.Equal("carga-2027.xlsx", result.GetProperty("fileName").GetString());
        Assert.Equal(1, result.GetProperty("created").GetInt32());
        Assert.Equal(27, result.GetProperty("carried").GetInt32());
        Assert.Equal(0, result.GetProperty("updated").GetInt32());
        Assert.Equal(0, result.GetProperty("unchanged").GetInt32());
        var batch = result.GetProperty("batchId").GetGuid();
        Assert.Equal(1, world.Versions.Bumps);

        var history = await JsonAsync(await world.Client.GetAsync(Base() + "/history?pageSize=100", Ct));
        var items = history.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(28, items.Count);
        Assert.All(items, item =>
        {
            Assert.Equal(batch, item.GetProperty("batchId").GetGuid());
            Assert.Equal("Excel", item.GetProperty("source").GetString());
            Assert.Equal("carga-2027.xlsx", item.GetProperty("fileName").GetString());
            Assert.Equal("Created", item.GetProperty("action").GetString());
        });

        var screen = await JsonAsync(await world.Client.GetAsync(Base() + "/", Ct));
        var smmlv = screen.GetProperty("parameters").EnumerateArray().Single(item => item.GetProperty("key").GetString() == "Smmlv");
        Assert.Equal("2027-01-01", smmlv.GetProperty("rows")[0].GetProperty("effectiveFrom").GetString());
        Assert.Equal(2_000_000m, smmlv.GetProperty("rows")[0].GetProperty("value").GetDecimal());
        Assert.Equal(0, screen.GetProperty("nextYearMissingCount").GetInt32());
    }

    [Fact]
    public async Task UnchangedRowsLeaveNoHistory()
    {
        await using var world = await OpenAsync();
        var screen = await JsonAsync(await world.Client.GetAsync(Base() + "/", Ct));
        var smmlv = screen.GetProperty("parameters").EnumerateArray().Single(item => item.GetProperty("key").GetString() == "Smmlv");
        var current = smmlv.GetProperty("current");
        using var workbook = new XLWorkbook(new MemoryStream(await DownloadTemplateAsync(world)));
        var sheet = workbook.Worksheet(LegalParameterImportColumns.ParametersSheet);
        // Una sola fila: la que ya existe, con el mismo valor.
        foreach (var row in sheet.RowsUsed().Skip(1).Where(item => item.Cell(1).GetString() != "Smmlv").ToList())
        {
            row.Cell(6).Clear();
        }

        var smmlvRow = sheet.RowsUsed().Skip(1).Single(item => item.Cell(1).GetString() == "Smmlv");
        smmlvRow.Cell(5).Value = DateTime.Parse(current.GetProperty("effectiveFrom").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        smmlvRow.Cell(6).Value = (double)current.GetProperty("value").GetDecimal();
        workbook.Worksheet(LegalParameterImportColumns.BracketsSheet).Clear();
        workbook.Worksheet(LegalParameterImportColumns.BracketsSheet).Cell(1, 1).Value = LegalParameterImportColumns.Key;
        workbook.Worksheet(LegalParameterImportColumns.BracketsSheet).Cell(1, 2).Value = LegalParameterImportColumns.EffectiveFrom;
        workbook.Worksheet(LegalParameterImportColumns.BracketsSheet).Cell(1, 3).Value = LegalParameterImportColumns.LowerBound;
        workbook.Worksheet(LegalParameterImportColumns.BracketsSheet).Cell(1, 4).Value = LegalParameterImportColumns.UpperBound;
        workbook.Worksheet(LegalParameterImportColumns.BracketsSheet).Cell(1, 5).Value = LegalParameterImportColumns.Rate;

        var result = await JsonAsync(await ImportAsync(world, Save(workbook)));

        Assert.Equal(0, result.GetProperty("created").GetInt32());
        Assert.Equal(0, result.GetProperty("carried").GetInt32());
        Assert.Equal(1, result.GetProperty("unchanged").GetInt32());
        Assert.Equal(0, await HistoryTotalAsync(world));
        Assert.Equal(0, world.Versions.Bumps);
    }

    [Fact]
    public async Task WithoutFileIs422FileRequired()
    {
        await using var world = await OpenAsync();

        var preview = await world.Client.PostAsync(
            Base() + "/import/preview", new MultipartFormDataContent { { new StringContent("x"), "other" } }, Ct);
        var import = await world.Client.PostAsync(
            Base() + "/import", new MultipartFormDataContent { { new StringContent("x"), "other" } }, Ct);

        await ProblemAsync(preview, HttpStatusCode.UnprocessableEntity, "hr.legal_parameter.import.file_required");
        await ProblemAsync(import, HttpStatusCode.UnprocessableEntity, "hr.legal_parameter.import.file_required");
    }

    [Fact]
    public async Task AnUnreadableFileIs422Unreadable()
    {
        await using var world = await OpenAsync();

        var response = await PreviewAsync(world, [1, 2, 3, 4, 5, 6, 7, 8]);

        await ProblemAsync(response, HttpStatusCode.UnprocessableEntity, "hr.legal_parameter.import.unreadable");
    }
}
