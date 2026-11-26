using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Modules.Hr.IntegrationTests.HrApiHarness;
using static Modules.Hr.IntegrationTests.HrApiSeeds;

namespace Modules.Hr.IntegrationTests;

/// <summary>
/// El ciclo de vida del contrato por HTTP: prórrogas, conversiones, terminar y reincorporar (spec 2026-11-20). El alta es la de
/// <see cref="HrApiSeeds.HireBody"/>: inicio 2026-01-15, fin 2027-01-14; el reloj, 2026-11-20 10:00 en Bogotá.
/// </summary>
public sealed class EmployeeContractApiTests : IClassFixture<HrApiFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly HrApiFixture _fixture;

    public EmployeeContractApiTests(HrApiFixture fixture) => _fixture = fixture;

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

    private static Task<HttpResponseMessage> RenewAsync(HrTenant tenant, JsonElement detail, string newEndDate) =>
        tenant.Hr.PostAsJsonAsync($"{ContractUrl(tenant, detail)}/renewals", new { newEndDate }, Ct);

    private static Task<HttpResponseMessage> TerminateAsync(
        HrTenant tenant, JsonElement detail, string date = "2026-11-30", string reason = "Resignation") =>
        tenant.Hr.PostAsJsonAsync($"{ContractUrl(tenant, detail)}/terminate", new { reason, date, notes = (string?)null }, Ct);

    private static Task<HttpResponseMessage> ConvertAsync(HrTenant tenant, JsonElement detail, Dictionary<string, object?> body) =>
        tenant.Hr.PostAsJsonAsync($"{ContractUrl(tenant, detail)}/conversion", body, Ct);

    private static Task<HttpResponseMessage> ReincorporateAsync(HrTenant tenant, JsonElement detail, Dictionary<string, object?> body) =>
        tenant.Hr.PostAsJsonAsync($"{EmployeeUrl(tenant, detail)}/reincorporate", body, Ct);

    private static long VersionOf(JsonElement detail) => detail.GetProperty("employee").GetProperty("version").GetInt64();

    // Review Focus 4: la Ley 2466/2025 bloquea, no avisa.
    [Fact]
    public async Task TheFourthShortRenewalNeedsOneYearAndTheCapBlocksPastFourYears()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var detail = await HireAsync(tenant, HireBody(setup, endDate: "2026-12-14"));

        foreach (var shortEnd in new[] { "2027-06-14", "2027-12-14", "2028-06-14" })
        {
            var renewed = await OkAsync(await RenewAsync(tenant, detail, shortEnd));
            Assert.True(renewed.GetProperty("contract").GetProperty("renewals")[0].GetProperty("short").GetBoolean());
        }

        var fourthShort = await ProblemAsync(await RenewAsync(tenant, detail, "2028-12-14"), HttpStatusCode.UnprocessableEntity);
        var oneYear = await OkAsync(await RenewAsync(tenant, detail, "2029-06-14"));
        // El tope se evalúa antes que el año mínimo: 2030-01-15 pasa de los cuatro años (2030-01-14).
        var pastTheCap = await ProblemAsync(await RenewAsync(tenant, detail, "2030-01-15"), HttpStatusCode.UnprocessableEntity);

        Assert.Equal("hr.contract.renewal_minimum_one_year", CodeOf(fourthShort));
        Assert.False(oneYear.GetProperty("contract").GetProperty("renewals")[0].GetProperty("short").GetBoolean());
        Assert.Equal("hr.contract.fixed_term_cap_exceeded", CodeOf(pastTheCap));
    }

    [Fact]
    public async Task ARenewalNotPastTheCurrentEndIs422OnNewEndDate()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var detail = await HireAsync(tenant, setup);

        var problem = await ProblemAsync(await RenewAsync(tenant, detail, "2027-01-14"), HttpStatusCode.UnprocessableEntity);

        Assert.True(HasError(problem, "newEndDate"));
    }

    [Fact]
    public async Task OnlyFixedTermRenews()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var detail = await HireAsync(tenant, HireBody(setup, duration: "Indefinite", endDate: null));

        var problem = await ProblemAsync(await RenewAsync(tenant, detail, "2027-12-14"), HttpStatusCode.UnprocessableEntity);

        Assert.Equal("hr.contract.not_fixed_term", CodeOf(problem));
    }

    [Fact]
    public async Task ConvertingToIndefiniteClearsTheEndAndKeepsTheStart()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var detail = await HireAsync(tenant, setup);

        var converted = await OkAsync(await tenant.Hr.PostAsync($"{ContractUrl(tenant, detail)}/convert-to-indefinite", null, Ct));

        var contract = converted.GetProperty("contract");
        Assert.Equal("Indefinite", contract.GetProperty("duration").GetString());
        Assert.Equal(JsonValueKind.Null, contract.GetProperty("endDate").ValueKind);
        Assert.Equal("2026-11-20", contract.GetProperty("convertedToIndefiniteOn").GetString());
        Assert.Equal("2026-01-15", contract.GetProperty("startDate").GetString());
        Assert.False(contract.GetProperty("renewalNoticeDue").GetBoolean());
    }

    [Fact]
    public async Task AConversionCreatesALinkedContract()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var detail = await HireAsync(tenant, HireBody(setup, contractType: "SenaApprenticeTeaching", amount: 1_000_000m));
        var oldId = detail.GetProperty("contract").GetProperty("id").GetGuid();

        var converted = await OkAsync(await ConvertAsync(
            tenant,
            detail,
            NewContractBody(setup, "2026-12-01", contractType: "SenaApprenticeProductive", duration: "FixedTerm", endDate: "2027-06-30", amount: 1_200_000m)));

        var contract = converted.GetProperty("contract");
        Assert.Equal(oldId, contract.GetProperty("previousContractId").GetGuid());
        Assert.Equal("SenaApprenticeProductive", contract.GetProperty("contractType").GetString());
        var previous = converted.GetProperty("previousContracts")[0];
        Assert.Equal("Converted", previous.GetProperty("status").GetString());
        Assert.Equal("2026-11-30", previous.GetProperty("endedOn").GetString());
        Assert.Equal("Active", converted.GetProperty("employee").GetProperty("status").GetString());
    }

    [Fact]
    public async Task AConversionStartingBeforeTheCurrentStartIs422()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var detail = await HireAsync(tenant, setup);

        var problem = await ProblemAsync(
            await ConvertAsync(tenant, detail, NewContractBody(setup, "2026-01-15")), HttpStatusCode.UnprocessableEntity);

        Assert.Equal("hr.contract.start_not_after_previous", CodeOf(problem));
    }

    [Fact]
    public async Task AConversionWithAScheduledRecordAfterTheCloseIs422RecordsAfterEnd()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var detail = await HireAsync(tenant, setup);
        await OkAsync(await tenant.Hr.PostAsJsonAsync(
            $"{ContractUrl(tenant, detail)}/salaries", new { effectiveDate = "2026-12-20", amount = 2_800_000m }, Ct));

        var problem = await ProblemAsync(
            await ConvertAsync(tenant, detail, NewContractBody(setup, "2026-12-01")), HttpStatusCode.UnprocessableEntity);

        Assert.Equal("hr.contract.records_after_end", CodeOf(problem));
    }

    // Review Focus 6: convertir no cambia de empresa; para eso se termina y se reincorpora.
    [Fact]
    public async Task AConversionCannotChangeTheCompany()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var second = await SeedSecondCompanyAsync(tenant);
        var detail = await HireAsync(tenant, setup);

        var problem = await ProblemAsync(
            await ConvertAsync(tenant, detail, NewContractBody(setup, "2026-12-01", companyId: second.Company.Id, workplaceId: second.WorkplaceId)),
            HttpStatusCode.UnprocessableEntity);

        Assert.Equal("hr.contract.company_change_not_allowed", CodeOf(problem));
        var again = await GetEmployeeAsync(tenant, detail);
        Assert.Equal(VersionOf(detail), VersionOf(again));
        Assert.Empty(again.GetProperty("previousContracts").EnumerateArray());
    }

    [Fact]
    public async Task AConversionKeepsTheCompany()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var detail = await HireAsync(tenant, setup);

        var converted = await OkAsync(await ConvertAsync(tenant, detail, NewContractBody(setup, "2026-12-01")));

        Assert.Equal(
            converted.GetProperty("contract").GetProperty("company").GetProperty("id").GetGuid(),
            converted.GetProperty("previousContracts")[0].GetProperty("company").GetProperty("id").GetGuid());
    }

    // D-12: convertir es el mismo empleador, así que no exige que la empresa esté activa.
    [Fact]
    public async Task AConversionInADeactivatedCompanyWorks()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var second = await SeedSecondCompanyAsync(tenant);
        var detail = await HireAsync(
            tenant, HireBody(setup, companyId: second.Company.Id, workplaceId: second.WorkplaceId, contractType: "SenaApprenticeTeaching", amount: 1_000_000m));
        await DeactivateCompanyAsync(tenant.Admin, tenant.Id, second.Company.Id);

        var converted = await OkAsync(await ConvertAsync(
            tenant,
            detail,
            NewContractBody(
                setup, "2026-12-01", companyId: second.Company.Id, contractType: "SenaApprenticeProductive", workplaceId: second.WorkplaceId, amount: 1_200_000m)));

        var company = converted.GetProperty("contract").GetProperty("company");
        Assert.Equal(second.Company.Id, company.GetProperty("id").GetGuid());
        Assert.False(company.GetProperty("isActive").GetBoolean());
    }

    [Fact]
    public async Task ATerminationInADeactivatedCompanyWorks()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var second = await SeedSecondCompanyAsync(tenant);
        var detail = await HireAsync(tenant, HireBody(setup, companyId: second.Company.Id, workplaceId: second.WorkplaceId));
        await DeactivateCompanyAsync(tenant.Admin, tenant.Id, second.Company.Id);

        var terminated = await OkAsync(await TerminateAsync(tenant, detail));

        Assert.Equal("Retired", terminated.GetProperty("employee").GetProperty("status").GetString());
    }

    [Fact]
    public async Task ReincorporatingInADeactivatedCompanyIs422()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var second = await SeedSecondCompanyAsync(tenant);
        var detail = await HireAsync(tenant, HireBody(setup, companyId: second.Company.Id, workplaceId: second.WorkplaceId));
        await OkAsync(await TerminateAsync(tenant, detail));
        await DeactivateCompanyAsync(tenant.Admin, tenant.Id, second.Company.Id);

        var problem = await ProblemAsync(
            await ReincorporateAsync(
                tenant, detail, NewContractBody(setup, "2026-12-15", companyId: second.Company.Id, workplaceId: second.WorkplaceId)),
            HttpStatusCode.UnprocessableEntity);

        Assert.True(HasError(problem, "contract.companyId"));
    }

    [Fact]
    public async Task ReincorporatingInAnotherCompanyChangesTheEmployer()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var second = await SeedSecondCompanyAsync(tenant);
        var detail = await HireAsync(tenant, setup);
        await OkAsync(await TerminateAsync(tenant, detail));

        var back = await OkAsync(await ReincorporateAsync(
            tenant, detail, NewContractBody(setup, "2026-12-15", companyId: second.Company.Id, workplaceId: second.WorkplaceId)));

        Assert.Equal(second.Company.Id, back.GetProperty("contract").GetProperty("company").GetProperty("id").GetGuid());
        Assert.Equal(setup.Company.Id, back.GetProperty("previousContracts")[0].GetProperty("company").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task ReincorporatingWithAWorkplaceOfAnotherCompanyIs422()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var second = await SeedSecondCompanyAsync(tenant);
        var detail = await HireAsync(tenant, setup);
        await OkAsync(await TerminateAsync(tenant, detail));

        var problem = await ProblemAsync(
            await ReincorporateAsync(tenant, detail, NewContractBody(setup, "2026-12-15", workplaceId: second.WorkplaceId)),
            HttpStatusCode.UnprocessableEntity);

        Assert.Equal("hr.assignment.workplace_other_company", CodeOf(problem));
        Assert.True(HasError(problem, "assignment.workplaceId"));
    }

    [Fact]
    public async Task TerminatingRetiresTheEmployee()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var detail = await HireAsync(tenant, setup);

        var terminated = await OkAsync(await TerminateAsync(tenant, detail, reason: "DismissalWithJustCause"));

        Assert.Equal(JsonValueKind.Null, terminated.GetProperty("contract").ValueKind);
        var termination = terminated.GetProperty("previousContracts")[0].GetProperty("termination");
        Assert.True(termination.GetProperty("justCause").GetBoolean());
        Assert.Equal("2026-11-30", termination.GetProperty("date").GetString());
        Assert.Equal("Retired", terminated.GetProperty("employee").GetProperty("status").GetString());
    }

    [Fact]
    public async Task ATerminationBeforeTheStartIs422OnDate()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var detail = await HireAsync(tenant, setup);

        var problem = await ProblemAsync(await TerminateAsync(tenant, detail, "2026-01-14"), HttpStatusCode.UnprocessableEntity);

        Assert.True(HasError(problem, "date"));
    }

    [Fact]
    public async Task WritesOnATerminatedContractAre422NotActive()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var detail = await HireAsync(tenant, setup);
        var terminated = await OkAsync(await TerminateAsync(tenant, detail));
        var closedUrl = $"{EmployeeUrl(tenant, detail)}/contracts/{terminated.GetProperty("previousContracts")[0].GetProperty("id").GetGuid()}";

        var salary = await ProblemAsync(
            await tenant.Hr.PostAsJsonAsync($"{closedUrl}/salaries", new { effectiveDate = "2026-12-01", amount = 2_800_000m }, Ct),
            HttpStatusCode.UnprocessableEntity);
        var renewal = await ProblemAsync(
            await tenant.Hr.PostAsJsonAsync($"{closedUrl}/renewals", new { newEndDate = "2027-12-14" }, Ct),
            HttpStatusCode.UnprocessableEntity);

        Assert.Equal("hr.contract.not_active", CodeOf(salary));
        Assert.Equal("hr.contract.not_active", CodeOf(renewal));
    }

    [Fact]
    public async Task ReincorporatingARetiredEmployeeCreatesANewContract()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var detail = await HireAsync(tenant, setup);
        await OkAsync(await TerminateAsync(tenant, detail));

        var back = await OkAsync(await ReincorporateAsync(tenant, detail, NewContractBody(setup, "2026-12-15")));

        Assert.Equal("Active", back.GetProperty("employee").GetProperty("status").GetString());
        Assert.Equal("2026-12-15", back.GetProperty("contract").GetProperty("startDate").GetString());
        Assert.Single(back.GetProperty("previousContracts").EnumerateArray());
    }

    // El historial dice de qué estado vuelve: un desactivado no es un retirado.
    [Theory]
    [InlineData(false, "Retired")]
    [InlineData(true, "Deactivated")]
    public async Task TheReincorporationHistoryRecordsTheRealPreviousStatus(bool deactivate, string expectedBefore)
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var detail = await HireAsync(tenant, setup);
        if (deactivate)
        {
            await OkAsync(await tenant.Hr.PostAsJsonAsync($"{EmployeeUrl(tenant, detail)}/deactivate", new { date = "2026-11-30" }, Ct));
        }
        else
        {
            await OkAsync(await TerminateAsync(tenant, detail));
        }

        await OkAsync(await ReincorporateAsync(tenant, detail, NewContractBody(setup, "2026-12-15")));

        var activity = await JsonOfAsync(await tenant.Hr.GetAsync($"{EmployeeUrl(tenant, detail)}/activity", Ct));
        var entry = activity.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("action").GetString() == "EmployeeReincorporated");
        var status = entry.GetProperty("changes").EnumerateArray().Single(change => change.GetProperty("field").GetString() == "status");
        Assert.Equal(expectedBefore, status.GetProperty("before").GetString());
        Assert.Equal("Active", status.GetProperty("after").GetString());
    }

    [Fact]
    public async Task ReincorporatingOnThePreviousEndIs422StartNotAfterPrevious()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var detail = await HireAsync(tenant, setup);
        await OkAsync(await TerminateAsync(tenant, detail));

        var problem = await ProblemAsync(
            await ReincorporateAsync(tenant, detail, NewContractBody(setup, "2026-11-30")), HttpStatusCode.UnprocessableEntity);

        Assert.Equal("hr.contract.start_not_after_previous", CodeOf(problem));
    }

    [Fact]
    public async Task ReincorporatingAnActiveEmployeeIs422AlreadyActive()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var detail = await HireAsync(tenant, setup);

        var problem = await ProblemAsync(
            await ReincorporateAsync(tenant, detail, NewContractBody(setup, "2026-12-15")), HttpStatusCode.UnprocessableEntity);

        Assert.Equal("hr.employee.already_active", CodeOf(problem));
    }

    [Fact]
    public async Task EveryContractWriteBumpsTheVersion()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var detail = await HireAsync(tenant, setup);
        var coordinator = await CreatedAsync(tenant.Hr, $"{tenant.Url}/positions", new { name = "Coordinador" });
        var url = ContractUrl(tenant, detail);

        var versions = new List<long> { VersionOf(detail) };
        versions.Add(VersionOf(await OkAsync(await tenant.Hr.PostAsJsonAsync($"{url}/salaries", new { effectiveDate = "2026-12-01", amount = 2_800_000m }, Ct))));
        versions.Add(VersionOf(await OkAsync(await tenant.Hr.PostAsJsonAsync(
            $"{url}/assignments",
            new { effectiveDate = "2026-12-01", positionId = coordinator.GetProperty("id").GetGuid(), areaId = setup.AreaId, workplaceId = setup.WorkplaceId },
            Ct))));
        versions.Add(VersionOf(await OkAsync(await RenewAsync(tenant, detail, "2027-12-14"))));
        versions.Add(VersionOf(await OkAsync(await tenant.Hr.PostAsync($"{url}/convert-to-indefinite", null, Ct))));

        Assert.Equal([1L, 2L, 3L, 4L, 5L], versions);
    }

    [Fact]
    public async Task ContractWritesAreAuditedAndLogged()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var detail = await HireAsync(tenant, setup);
        var contractId = detail.GetProperty("contract").GetProperty("id").GetGuid().ToString();
        var coordinator = await CreatedAsync(tenant.Hr, $"{tenant.Url}/positions", new { name = "Coordinador" });
        var url = ContractUrl(tenant, detail);
        await OkAsync(await tenant.Hr.PostAsJsonAsync($"{url}/salaries", new { effectiveDate = "2026-12-01", amount = 2_800_000m }, Ct));
        await OkAsync(await tenant.Hr.PostAsJsonAsync(
            $"{url}/assignments",
            new { effectiveDate = "2026-12-01", positionId = coordinator.GetProperty("id").GetGuid(), areaId = setup.AreaId, workplaceId = setup.WorkplaceId },
            Ct));
        await OkAsync(await RenewAsync(tenant, detail, "2027-12-14"));

        foreach (var action in new[] { "salary_created", "assignment_created", "renewed" })
        {
            Assert.Equal(1, await CountAuditEntriesAsync(_fixture.Factory, $"hr.contract.{action}", contractId));
        }

        var activity = await JsonOfAsync(await tenant.Hr.GetAsync($"{EmployeeUrl(tenant, detail)}/activity", Ct));
        Assert.Equal(4, activity.GetProperty("total").GetInt32());
        Assert.Equal(
            ["ContractRenewed", "AssignmentCreated", "SalaryCreated", "EmployeeCreated"],
            activity.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("action").GetString()));
    }
}
