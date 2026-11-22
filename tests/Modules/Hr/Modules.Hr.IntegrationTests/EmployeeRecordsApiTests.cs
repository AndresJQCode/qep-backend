using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Hr.Application;
using static Modules.Hr.IntegrationTests.HrApiHarness;
using static Modules.Hr.IntegrationTests.HrApiSeeds;

namespace Modules.Hr.IntegrationTests;

/// <summary>
/// Los registros con fecha de vigencia del contrato por HTTP: salarios y cargo y lugar, con sus documentos (spec 2026-11-20,
/// «Historiales»). El alta es la de <see cref="HrApiSeeds.HireBody"/>: inicio 2026-01-15, fin 2027-01-14; el reloj, 2026-11-20 10:00
/// en Bogotá, salvo que la prueba lo mueva.
/// </summary>
public sealed class EmployeeRecordsApiTests : IClassFixture<HrApiFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly HrApiFixture _fixture;

    public EmployeeRecordsApiTests(HrApiFixture fixture) => _fixture = fixture;

    private static string ContractUrl(HrTenant tenant, JsonElement detail) =>
        $"{EmployeeUrl(tenant, detail)}/contracts/{detail.GetProperty("contract").GetProperty("id").GetGuid()}";

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        return await JsonOfAsync(response);
    }

    private static async Task<JsonElement> OkAsync(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode == HttpStatusCode.OK, await BodyAsync(response));
        return await JsonOfAsync(response);
    }

    private static Task<HttpResponseMessage> AddSalaryAsync(
        HrTenant tenant, JsonElement detail, string effectiveDate, decimal amount, bool acknowledge = false) =>
        tenant.Hr.PostAsJsonAsync(
            $"{ContractUrl(tenant, detail)}/salaries",
            new { effectiveDate, amount, teleworker = false, acknowledgeBelowMinimum = acknowledge },
            Ct);

    private static Task<HttpResponseMessage> AddAssignmentAsync(
        HrTenant tenant, JsonElement detail, string effectiveDate, Guid positionId, Guid areaId, Guid workplaceId) =>
        tenant.Hr.PostAsJsonAsync(
            $"{ContractUrl(tenant, detail)}/assignments", new { effectiveDate, positionId, areaId, workplaceId }, Ct);

    private static Task<HttpResponseMessage> RewriteAsync(
        HrTenant tenant, JsonElement detail, Guid salaryId, decimal amount, bool? confirm, string? ifMatch) =>
        PutAsync(
            tenant.Hr,
            $"{ContractUrl(tenant, detail)}/salaries/{salaryId}",
            new { amount, teleworker = false, acknowledgeBelowMinimum = false, confirmRewrite = confirm },
            ifMatch);

    private static Guid FirstSalaryId(JsonElement detail) =>
        detail.GetProperty("contract").GetProperty("salaries")[0].GetProperty("id").GetGuid();

    private static long VersionOf(JsonElement detail) => detail.GetProperty("employee").GetProperty("version").GetInt64();

    [Fact]
    public async Task ARaiseIsANewRecordAndTheOldOneStays()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var detail = await HireAsync(tenant, setup);

        var raised = await OkAsync(await AddSalaryAsync(tenant, detail, "2026-12-01", 2_800_000m));

        var contract = raised.GetProperty("contract");
        Assert.Equal(2, contract.GetProperty("salaries").GetArrayLength());
        Assert.True(contract.GetProperty("salaries")[0].GetProperty("scheduled").GetBoolean());
        Assert.Equal(2_800_000m, contract.GetProperty("salaries")[0].GetProperty("amount").GetDecimal());
        Assert.Equal(2_500_000m, contract.GetProperty("salaries")[1].GetProperty("amount").GetDecimal());
        Assert.Equal(2_500_000m, contract.GetProperty("current").GetProperty("salary").GetDecimal());
        Assert.Equal(2, VersionOf(raised));
    }

    [Fact]
    public async Task ASalaryOnOrBeforeTheLastIs422()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var detail = await HireAsync(tenant, setup);

        var problem = await ProblemAsync(await AddSalaryAsync(tenant, detail, "2026-01-15", 2_800_000m), HttpStatusCode.UnprocessableEntity);

        Assert.Equal("hr.salary.effective_date_not_after_last", CodeOf(problem));
    }

    [Fact]
    public async Task ASalaryAfterTheEndDateIs422OnEffectiveDate()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var detail = await HireAsync(tenant, setup);

        var problem = await ProblemAsync(await AddSalaryAsync(tenant, detail, "2027-02-01", 2_800_000m), HttpStatusCode.UnprocessableEntity);

        Assert.True(HasError(problem, "effectiveDate"));
    }

    // Review Focus 1: «hoy» es el día del tenant. A las 23:30 de Bogotá del 30 de noviembre (04:30Z del 1 de diciembre) un salario del
    // 1 de diciembre sigue programado.
    [Fact]
    public async Task ASalaryEffectiveTomorrowInTheTenantZoneIsStillScheduled()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var detail = await HireAsync(tenant, setup);
        _fixture.Clock.UtcNow = new DateTimeOffset(2026, 12, 1, 4, 30, 0, TimeSpan.Zero);

        var scheduled = await OkAsync(await AddSalaryAsync(tenant, detail, "2026-12-01", 2_800_000m));

        var contract = scheduled.GetProperty("contract");
        Assert.True(contract.GetProperty("salaries")[0].GetProperty("scheduled").GetBoolean());
        Assert.Equal(2_500_000m, contract.GetProperty("current").GetProperty("salary").GetDecimal());

        _fixture.Clock.UtcNow = new DateTimeOffset(2026, 12, 1, 5, 0, 0, TimeSpan.Zero);
        var later = await GetEmployeeAsync(tenant, detail);

        var current = later.GetProperty("contract");
        Assert.Equal(2_800_000m, current.GetProperty("current").GetProperty("salary").GetDecimal());
        Assert.True(current.GetProperty("salaries")[0].GetProperty("current").GetBoolean());
        Assert.False(current.GetProperty("salaries")[0].GetProperty("scheduled").GetBoolean());
    }

    [Fact]
    public async Task ARewriteWithoutConfirmationIs422RewriteUnconfirmed()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var detail = await HireAsync(tenant, setup);

        var problem = await ProblemAsync(
            await RewriteAsync(tenant, detail, FirstSalaryId(detail), 2_600_000m, confirm: null, Version(detail)),
            HttpStatusCode.UnprocessableEntity);

        Assert.Equal("hr.salary.rewrite_unconfirmed", CodeOf(problem));
    }

    [Fact]
    public async Task ARewriteNeedsIfMatch()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var detail = await HireAsync(tenant, setup);

        var missing = await RewriteAsync(tenant, detail, FirstSalaryId(detail), 2_600_000m, confirm: true, null);
        var stale = await RewriteAsync(tenant, detail, FirstSalaryId(detail), 2_600_000m, confirm: true, "\"9\"");

        Assert.Equal(HttpStatusCode.PreconditionRequired, missing.StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
    }

    [Fact]
    public async Task ARewriteKeepsTheDateAndChangesTheAmount()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var detail = await HireAsync(tenant, setup);

        var rewritten = await OkAsync(await RewriteAsync(tenant, detail, FirstSalaryId(detail), 2_600_000m, confirm: true, Version(detail)));

        var record = Assert.Single(rewritten.GetProperty("contract").GetProperty("salaries").EnumerateArray());
        Assert.Equal("2026-01-15", record.GetProperty("effectiveDate").GetString());
        Assert.Equal(2_600_000m, record.GetProperty("amount").GetDecimal());
        Assert.Equal(2, VersionOf(rewritten));
    }

    [Fact]
    public async Task ABelowMinimumRaiseNeedsAcknowledgement()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var detail = await HireAsync(tenant, setup);

        var rejected = await ProblemAsync(await AddSalaryAsync(tenant, detail, "2026-12-01", 1_700_000m), HttpStatusCode.UnprocessableEntity);
        var accepted = await OkAsync(await AddSalaryAsync(tenant, detail, "2026-12-01", 1_700_000m, acknowledge: true));

        Assert.Equal("hr.salary.below_minimum_unacknowledged", CodeOf(rejected));
        Assert.True(accepted.GetProperty("contract").GetProperty("salaries")[0].GetProperty("belowMinimumAcknowledged").GetBoolean());
    }

    [Fact]
    public async Task AnIntegralRecordBelowTheFloorIs422()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var detail = await HireAsync(tenant, HireBody(setup, salaryType: "Integral", amount: 25_000_000m));

        var problem = await ProblemAsync(
            await AddSalaryAsync(tenant, detail, "2026-12-01", 20_000_000m, acknowledge: true), HttpStatusCode.UnprocessableEntity);

        Assert.Equal("hr.salary.below_integral_floor", CodeOf(problem));
    }

    // Review Focus 3: el lugar de trabajo sólo cambia el día 1 del mes; un cambio de cargo, cualquier día.
    [Fact]
    public async Task AWorkplaceChangeMidMonthIs422AndAPositionChangeIsNot()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var detail = await HireAsync(tenant, setup);
        var office = await CreatedAsync(
            tenant.Hr, $"{tenant.Url}/workplaces", new { name = "Oficina", siteId = setup.SiteId, ciiuCode = "4711", riskClass = "I", highRiskPension = false });
        var coordinator = await CreatedAsync(tenant.Hr, $"{tenant.Url}/positions", new { name = "Coordinador" });

        var workplaceChange = await ProblemAsync(
            await AddAssignmentAsync(tenant, detail, "2026-12-15", setup.PositionId, setup.AreaId, office.GetProperty("id").GetGuid()),
            HttpStatusCode.UnprocessableEntity);
        var positionChange = await OkAsync(
            await AddAssignmentAsync(tenant, detail, "2026-12-15", coordinator.GetProperty("id").GetGuid(), setup.AreaId, setup.WorkplaceId));

        Assert.Equal("hr.assignment.site_change_not_first_of_month", CodeOf(workplaceChange));
        var latest = positionChange.GetProperty("contract").GetProperty("assignments")[0];
        Assert.True(latest.GetProperty("scheduled").GetBoolean());
        Assert.Equal("Coordinador", latest.GetProperty("position").GetProperty("name").GetString());
    }

    [Fact]
    public async Task AWorkplaceChangeOnTheFirstIsAccepted()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var detail = await HireAsync(tenant, setup);
        var branch = await CreatedAsync(
            tenant.Hr, $"{tenant.Url}/sites", new { companyId = setup.Company.Id, name = "Sucursal", address = (string?)null, cityId = (Guid?)null, compensationFund = (string?)null });
        var workshop = await CreatedAsync(
            tenant.Hr, $"{tenant.Url}/workplaces", new { name = "Taller", siteId = branch.GetProperty("id").GetGuid(), ciiuCode = "4711", riskClass = "II", highRiskPension = false });

        var moved = await OkAsync(
            await AddAssignmentAsync(tenant, detail, "2026-12-01", setup.PositionId, setup.AreaId, workshop.GetProperty("id").GetGuid()));

        var latest = moved.GetProperty("contract").GetProperty("assignments")[0];
        Assert.Equal("Sucursal", latest.GetProperty("site").GetProperty("name").GetString());
        Assert.Equal("Taller", latest.GetProperty("workplace").GetProperty("name").GetString());
    }

    [Fact]
    public async Task ANoOpAssignmentIs422Unchanged()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var detail = await HireAsync(tenant, setup);

        var problem = await ProblemAsync(
            await AddAssignmentAsync(tenant, detail, "2026-12-01", setup.PositionId, setup.AreaId, setup.WorkplaceId),
            HttpStatusCode.UnprocessableEntity);

        Assert.Equal("hr.assignment.unchanged", CodeOf(problem));
    }

    [Fact]
    public async Task AnArchivedPositionIs422OnPositionId()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var detail = await HireAsync(tenant, setup);
        var spare = await CreatedAsync(tenant.Hr, $"{tenant.Url}/positions", new { name = "Auxiliar" });
        var spareId = spare.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await tenant.Hr.PostAsync($"{tenant.Url}/positions/{spareId}/archive", null, Ct)).StatusCode);

        var problem = await ProblemAsync(
            await AddAssignmentAsync(tenant, detail, "2026-12-01", spareId, setup.AreaId, setup.WorkplaceId),
            HttpStatusCode.UnprocessableEntity);

        Assert.True(HasError(problem, "positionId"));
    }

    // Review Focus 6: la empresa del contrato es fija y manda sobre el lugar.
    [Fact]
    public async Task AWorkplaceOfAnotherCompanyIs422()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var second = await SeedSecondCompanyAsync(tenant);
        var detail = await HireAsync(tenant, setup);

        var problem = await ProblemAsync(
            await AddAssignmentAsync(tenant, detail, "2026-12-01", setup.PositionId, setup.AreaId, second.WorkplaceId),
            HttpStatusCode.UnprocessableEntity);

        Assert.Equal("hr.assignment.workplace_other_company", CodeOf(problem));
        Assert.True(HasError(problem, "workplaceId"));
        Assert.Equal(VersionOf(detail), VersionOf(await GetEmployeeAsync(tenant, detail)));
    }

    // D-12: una empresa desactivada sólo deja de ofrecerse para vínculos nuevos; lo de un contrato existente sigue funcionando.
    [Fact]
    public async Task ADeactivatedCompanyKeepsItsContract()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var second = await SeedSecondCompanyAsync(tenant);
        var coordinator = await CreatedAsync(tenant.Hr, $"{tenant.Url}/positions", new { name = "Coordinador" });
        var detail = await HireAsync(tenant, HireBody(setup, companyId: second.Company.Id, workplaceId: second.WorkplaceId));
        await DeactivateCompanyAsync(tenant.Admin, tenant.Id, second.Company.Id);

        var shown = await GetEmployeeAsync(tenant, detail);
        Assert.False(shown.GetProperty("contract").GetProperty("company").GetProperty("isActive").GetBoolean());

        await OkAsync(await AddSalaryAsync(tenant, detail, "2026-12-01", 2_800_000m));
        await OkAsync(await AddAssignmentAsync(tenant, detail, "2026-12-01", coordinator.GetProperty("id").GetGuid(), setup.AreaId, second.WorkplaceId));
        await OkAsync(await tenant.Hr.PostAsJsonAsync($"{ContractUrl(tenant, detail)}/renewals", new { newEndDate = "2027-12-14" }, Ct));
        await OkAsync(await tenant.Hr.PostAsync($"{ContractUrl(tenant, detail)}/convert-to-indefinite", null, Ct));
    }

    [Fact]
    public async Task ADocumentCanBeAttachedToARecordLater()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var detail = await HireAsync(tenant, setup);
        var fileId = _fixture.Documents.Seed(Guid.CreateVersion7(), HrDocumentState.Available);

        var attached = await OkAsync(await tenant.Hr.PutAsJsonAsync(
            $"{ContractUrl(tenant, detail)}/salaries/{FirstSalaryId(detail)}/document", new { fileId }, Ct));

        var document = attached.GetProperty("contract").GetProperty("salaries")[0].GetProperty("document");
        Assert.Equal("contrato.pdf", document.GetProperty("fileName").GetString());
        Assert.Equal(fileId, document.GetProperty("fileId").GetGuid());

        var removed = await OkAsync(await tenant.Hr.PutAsJsonAsync(
            $"{ContractUrl(tenant, detail)}/salaries/{FirstSalaryId(detail)}/document", new { fileId = (Guid?)null }, Ct));
        Assert.Equal(JsonValueKind.Null, removed.GetProperty("contract").GetProperty("salaries")[0].GetProperty("document").ValueKind);
    }

    [Fact]
    public async Task ARejectedDocumentIs422AndWritesNothing()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var detail = await HireAsync(tenant, setup);
        var rejected = _fixture.Documents.Seed(Guid.CreateVersion7(), HrDocumentState.Rejected);
        var missing = _fixture.Documents.Seed(Guid.CreateVersion7(), HrDocumentState.NotUploaded);

        var first = await ProblemAsync(
            await tenant.Hr.PutAsJsonAsync($"{ContractUrl(tenant, detail)}/document", new { fileId = rejected }, Ct),
            HttpStatusCode.UnprocessableEntity);
        var second = await ProblemAsync(
            await tenant.Hr.PutAsJsonAsync($"{ContractUrl(tenant, detail)}/document", new { fileId = missing }, Ct),
            HttpStatusCode.UnprocessableEntity);
        var inCreation = await ProblemAsync(
            await AddSalaryWithDocumentAsync(tenant, detail, rejected), HttpStatusCode.UnprocessableEntity);

        Assert.Equal("hr.file.rejected", CodeOf(first));
        Assert.Equal("hr.file.not_found", CodeOf(second));
        Assert.Equal("hr.file.rejected", CodeOf(inCreation));
        Assert.Equal(VersionOf(detail), VersionOf(await GetEmployeeAsync(tenant, detail)));
    }

    private static Task<HttpResponseMessage> AddSalaryWithDocumentAsync(HrTenant tenant, JsonElement detail, Guid fileId) =>
        tenant.Hr.PostAsJsonAsync(
            $"{ContractUrl(tenant, detail)}/salaries",
            new { effectiveDate = "2026-12-01", amount = 2_800_000m, documentFileId = fileId },
            Ct);

    [Fact]
    public async Task AnUnknownSalaryIs404()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var detail = await HireAsync(tenant, setup);

        var rewrite = await ProblemAsync(
            await RewriteAsync(tenant, detail, Guid.NewGuid(), 2_600_000m, confirm: true, Version(detail)), HttpStatusCode.NotFound);
        var document = await ProblemAsync(
            await tenant.Hr.PutAsJsonAsync($"{ContractUrl(tenant, detail)}/salaries/{Guid.NewGuid()}/document", new { fileId = (Guid?)null }, Ct),
            HttpStatusCode.NotFound);

        Assert.Equal("hr.salary.not_found", CodeOf(rewrite));
        Assert.Equal("hr.salary.not_found", CodeOf(document));
    }

    [Fact]
    public async Task AContractOfAnotherEmployeeIs404()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var first = await HireAsync(tenant, setup, "1001");
        var second = await HireAsync(tenant, setup, "1002");
        var foreignContract = second.GetProperty("contract").GetProperty("id").GetGuid();

        var response = await tenant.Hr.PostAsJsonAsync(
            $"{EmployeeUrl(tenant, first)}/contracts/{foreignContract}/salaries",
            new { effectiveDate = "2026-12-01", amount = 2_800_000m },
            Ct);

        Assert.Equal("hr.contract.not_found", CodeOf(await ProblemAsync(response, HttpStatusCode.NotFound)));
    }
}
