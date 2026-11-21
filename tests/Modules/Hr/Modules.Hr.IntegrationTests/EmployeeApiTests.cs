using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Hr.Application;
using static Modules.Hr.IntegrationTests.HrApiHarness;
using static Modules.Hr.IntegrationTests.HrApiSeeds;

namespace Modules.Hr.IntegrationTests;

/// <summary>
/// El alta, la ficha, el pago, la desactivación, la eliminación y el historial de acciones de los empleados por HTTP (spec
/// 2026-11-20). El reloj del tenant está en 2026-11-20 10:00 (Bogotá); la configuración se siembra por la API real.
/// </summary>
public sealed class EmployeeApiTests : IClassFixture<HrApiFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly HrApiFixture _fixture;

    public EmployeeApiTests(HrApiFixture fixture) => _fixture = fixture;

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        return await JsonOfAsync(response);
    }

    private static async Task<JsonElement> ListAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await JsonOfAsync(response);
    }

    [Fact]
    public async Task CreatesAnEmployeeWithTheFullContract()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);

        var detail = await HireAsync(tenant, setup);

        var employee = detail.GetProperty("employee");
        Assert.Equal("Active", employee.GetProperty("status").GetString());
        Assert.Equal(1, employee.GetProperty("version").GetInt64());
        Assert.Equal(["Sunday"], employee.GetProperty("restDays").EnumerateArray().Select(day => day.GetString()));
        Assert.Equal("CO", employee.GetProperty("country").GetString());
        // La caja de compensación es de la sede, no de la ficha (decisión del owner, 2026-11-21).
        Assert.False(employee.TryGetProperty("compensationFund", out _));
        Assert.True(detail.GetProperty("canReadContract").GetBoolean());
        Assert.True(detail.GetProperty("canDelete").GetBoolean());
        var contract = detail.GetProperty("contract");
        Assert.Equal(2_500_000m, contract.GetProperty("current").GetProperty("salary").GetDecimal());
        Assert.Equal("Principal", contract.GetProperty("current").GetProperty("site").GetProperty("name").GetString());
        Assert.Single(contract.GetProperty("salaries").EnumerateArray());
        Assert.Single(contract.GetProperty("assignments").EnumerateArray());
        Assert.Equal("Comercializadora QEP S.A.S.", contract.GetProperty("company").GetProperty("name").GetString());
        Assert.Equal(setup.Company.TaxId, contract.GetProperty("company").GetProperty("taxId").GetString());
        Assert.Empty(detail.GetProperty("previousContracts").EnumerateArray());
    }

    [Fact]
    public async Task CreatingWithoutSetupIs422SetupIncomplete()
    {
        using var tenant = await NewTenantAsync(_fixture);
        // Sin cargos: hay empresa, sede y centro, pero falta un cargo.
        var first = await SeedCompanyAsync(tenant, "Comercializadora QEP S.A.S.", "Principal", "Bodega");
        var incomplete = new SeededSetup(first.Company, first.SiteId, first.WorkplaceId, Guid.NewGuid(), Guid.NewGuid());

        var withoutPositions = await PostEmployeeAsync(tenant, HireBody(incomplete));

        Assert.Equal("hr.settings.setup_incomplete", CodeOf(await ProblemAsync(withoutPositions, HttpStatusCode.UnprocessableEntity)));

        // Con todo configurado, pero la única empresa desactivada.
        await CreatedAsync(tenant.Hr, $"{tenant.Url}/positions", new { name = "Analista" });
        await DeactivateCompanyAsync(tenant.Admin, tenant.Id, first.Company.Id);

        var withoutCompany = await PostEmployeeAsync(tenant, HireBody(incomplete));

        Assert.Equal("hr.settings.setup_incomplete", CodeOf(await ProblemAsync(withoutCompany, HttpStatusCode.UnprocessableEntity)));
    }

    [Fact]
    public async Task TheCountryDefaultsToTheTenants()
    {
        using var tenant = await NewTenantAsync(_fixture, country: "MX");
        var setup = await SeedSetupAsync(tenant);

        var detail = await HireAsync(tenant, HireBody(setup, documentType: "PPT", country: null));

        Assert.Equal("MX", detail.GetProperty("employee").GetProperty("country").GetString());
    }

    [Fact]
    public async Task ACcWithAnotherCountryIs422OnCountry()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);

        var problem = await ProblemAsync(
            await PostEmployeeAsync(tenant, HireBody(setup, documentType: "CC", country: "VE")), HttpStatusCode.UnprocessableEntity);

        Assert.True(HasError(problem, "country"));
    }

    [Fact]
    public async Task AForeignDocumentWithColombiaIs422OnCountry()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);

        var problem = await ProblemAsync(
            await PostEmployeeAsync(tenant, HireBody(setup, documentType: "CE", country: "CO")), HttpStatusCode.UnprocessableEntity);

        Assert.True(HasError(problem, "country"));
    }

    [Fact]
    public async Task AnUnknownCountryIs422OnCountry()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);

        var problem = await ProblemAsync(
            await PostEmployeeAsync(tenant, HireBody(setup, documentType: "PA", country: "XX")), HttpStatusCode.UnprocessableEntity);

        Assert.True(HasError(problem, "country"));
    }

    [Fact]
    public async Task ForeignPensionExemptWithColombiaIs422()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);

        var problem = await ProblemAsync(
            await PostEmployeeAsync(tenant, HireBody(setup, foreignPensionExempt: true)), HttpStatusCode.UnprocessableEntity);
        Assert.True(HasError(problem, "foreignPensionExempt"));

        var created = await HireAsync(
            tenant, HireBody(setup, documentType: "PPT", country: "VE", foreignPensionExempt: true, documentNumber: "7001"));
        Assert.True(created.GetProperty("employee").GetProperty("foreignPensionExempt").GetBoolean());
    }

    // El historial deja el país entre los datos personales (no sensibles): lo ve quien no puede leer contratos.
    [Fact]
    public async Task ChangingTheCountryIsLoggedAsPersonalData()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var body = HireBody(setup, documentType: "PPT", country: "PE");
        var detail = await HireAsync(tenant, body);
        body["country"] = "VE";

        var updated = await PutAsync(tenant.Hr, EmployeeUrl(tenant, detail), UpdateBody(body), Version(detail));

        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        using var reader = tenant.Client(HrPermissions.EmployeeRead);
        var activity = await ListAsync(reader, $"{EmployeeUrl(tenant, detail)}/activity");
        var entry = activity.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("action").GetString() == "PersonalDataUpdated");
        var change = Assert.Single(entry.GetProperty("changes").EnumerateArray());
        Assert.Equal("country", change.GetProperty("field").GetString());
        Assert.Equal("PE", change.GetProperty("before").GetString());
        Assert.Equal("VE", change.GetProperty("after").GetString());
    }

    [Fact]
    public async Task AContractNeedsAnActiveCompany()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var second = await SeedSecondCompanyAsync(tenant);
        await DeactivateCompanyAsync(tenant.Admin, tenant.Id, second.Company.Id);
        using var other = await NewTenantAsync(_fixture);
        var foreign = await CreateCompanyAsync(other.Admin, other.Id, "Empresa de otro tenant");

        var inactive = await ProblemAsync(
            await PostEmployeeAsync(tenant, HireBody(setup, companyId: second.Company.Id, workplaceId: second.WorkplaceId)),
            HttpStatusCode.UnprocessableEntity);
        var elsewhere = await ProblemAsync(
            await PostEmployeeAsync(tenant, HireBody(setup, companyId: foreign.Id)), HttpStatusCode.UnprocessableEntity);

        Assert.True(HasError(inactive, "contract.contract.companyId"));
        Assert.True(HasError(elsewhere, "contract.contract.companyId"));
    }

    // D-11: una empresa con RRHH no se borra, y el empleado sigue mostrando su empresa.
    [Fact]
    public async Task DeletingACompanyWithAnEmployeeIs422InUse()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var second = await SeedSecondCompanyAsync(tenant);
        var detail = await HireAsync(tenant, HireBody(setup, companyId: second.Company.Id, workplaceId: second.WorkplaceId));

        var archive = await tenant.Hr.PostAsync($"{tenant.Url}/sites/{second.SiteId}/archive", null, Ct);
        var deleteSite = await tenant.Hr.DeleteAsync($"{tenant.Url}/sites/{second.SiteId}", Ct);
        var deleteCompany = await tenant.Admin.DeleteAsync($"/api/v1/tenants/{tenant.Id}/companies/{second.Company.Id}", Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, archive.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, deleteSite.StatusCode);
        Assert.Equal("companies.company.in_use", CodeOf(await ProblemAsync(deleteCompany, HttpStatusCode.UnprocessableEntity)));
        var again = await GetEmployeeAsync(tenant, detail);
        Assert.Equal(second.Company.Id, again.GetProperty("contract").GetProperty("company").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task AWorkplaceOfAnotherCompanyIs422OnTheNestedKey()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var second = await SeedSecondCompanyAsync(tenant);

        var problem = await ProblemAsync(
            await PostEmployeeAsync(tenant, HireBody(setup, workplaceId: second.WorkplaceId)), HttpStatusCode.UnprocessableEntity);

        Assert.Equal("hr.assignment.workplace_other_company", CodeOf(problem));
        Assert.True(HasError(problem, "contract.assignment.workplaceId"));
    }

    [Fact]
    public async Task TheListShowsAndFiltersByCompany()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var second = await SeedSecondCompanyAsync(tenant);
        await HireAsync(tenant, HireBody(setup, documentNumber: "1001", lastName: "Alvarez"));
        var other = await HireAsync(
            tenant, HireBody(setup, companyId: second.Company.Id, workplaceId: second.WorkplaceId, documentNumber: "1002", lastName: "Botero"));
        var retired = await HireAsync(tenant, HireBody(setup, documentNumber: "1003", lastName: "Cano"));
        await tenant.Hr.PostAsJsonAsync($"{EmployeeUrl(tenant, retired)}/deactivate", new { date = "2026-11-30" }, Ct);

        var all = await ListAsync(tenant.Hr, $"{tenant.Url}/employees");
        var bySecond = await ListAsync(tenant.Hr, $"{tenant.Url}/employees?companyId={second.Company.Id}");

        var items = all.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(["Comercializadora QEP S.A.S.", "Servicios del Norte S.A.S.", null], items.Select(item => item.GetProperty("company").GetString()));
        var only = Assert.Single(bySecond.GetProperty("items").EnumerateArray());
        Assert.Equal(other.GetProperty("employee").GetProperty("id").GetGuid(), only.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task AnArchivedPositionIs422OnTheNestedKey()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var spare = await CreatedAsync(tenant.Hr, $"{tenant.Url}/positions", new { name = "Auxiliar" });
        Assert.Equal(HttpStatusCode.OK, (await tenant.Hr.PostAsync($"{tenant.Url}/positions/{setup.PositionId}/archive", null, Ct)).StatusCode);

        var problem = await ProblemAsync(
            await PostEmployeeAsync(tenant, HireBody(setup)), HttpStatusCode.UnprocessableEntity);
        var created = await HireAsync(tenant, HireBody(setup, positionId: spare.GetProperty("id").GetGuid()));

        Assert.True(HasError(problem, "contract.assignment.positionId"));
        Assert.Equal("Auxiliar", created.GetProperty("contract").GetProperty("current").GetProperty("position").GetProperty("name").GetString());
    }

    [Fact]
    public async Task ABelowMinimumHireNeedsAcknowledgement()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);

        var rejected = await ProblemAsync(
            await PostEmployeeAsync(tenant, HireBody(setup, amount: 1_750_904m)), HttpStatusCode.UnprocessableEntity);
        var created = await HireAsync(tenant, HireBody(setup, amount: 1_750_904m, acknowledgeBelowMinimum: true));

        Assert.Equal("hr.salary.below_minimum_unacknowledged", CodeOf(rejected));
        Assert.True(created.GetProperty("contract").GetProperty("salaries")[0].GetProperty("belowMinimumAcknowledged").GetBoolean());
    }

    [Fact]
    public async Task AnIntegralHireBelowTheFloorIs422()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);

        var problem = await ProblemAsync(
            await PostEmployeeAsync(tenant, HireBody(setup, salaryType: "Integral", amount: 20_000_000m, acknowledgeBelowMinimum: true)),
            HttpStatusCode.UnprocessableEntity);

        Assert.Equal("hr.salary.below_integral_floor", CodeOf(problem));
    }

    [Fact]
    public async Task AFixedTermPastFourYearsIs422CapExceeded()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);

        var problem = await ProblemAsync(
            await PostEmployeeAsync(tenant, HireBody(setup, endDate: "2030-01-15")), HttpStatusCode.UnprocessableEntity);

        Assert.Equal("hr.contract.fixed_term_cap_exceeded", CodeOf(problem));
    }

    [Fact]
    public async Task TheListFiltersByStatusAndKeepsTheSummary()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        await HireAsync(tenant, setup, "1001");
        var closing = await HireAsync(tenant, setup, "1002");
        await tenant.Hr.PostAsJsonAsync($"{EmployeeUrl(tenant, closing)}/deactivate", new { date = "2026-11-30" }, Ct);

        var deactivated = await ListAsync(tenant.Hr, $"{tenant.Url}/employees?status=Deactivated");

        Assert.Single(deactivated.GetProperty("items").EnumerateArray());
        Assert.Equal(1, deactivated.GetProperty("total").GetInt32());
        var summary = deactivated.GetProperty("summary");
        Assert.Equal((1, 0, 1), (summary.GetProperty("active").GetInt32(), summary.GetProperty("retired").GetInt32(), summary.GetProperty("deactivated").GetInt32()));
        var bad = await tenant.Hr.GetAsync($"{tenant.Url}/employees?status=Fired&pageSize=500", Ct);
        var problem = await ProblemAsync(bad, HttpStatusCode.UnprocessableEntity);
        Assert.True(HasError(problem, "status"));
        Assert.True(HasError(problem, "pageSize"));
    }

    [Fact]
    public async Task ExpiringSoonListsOnlyFixedTermWithinThirtyDays()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var soon = await HireAsync(tenant, HireBody(setup, documentNumber: "1001", endDate: "2026-12-10"));
        await HireAsync(tenant, HireBody(setup, documentNumber: "1002", endDate: "2027-01-14"));
        await HireAsync(tenant, HireBody(setup, documentNumber: "1003", duration: "Indefinite", endDate: null));

        var list = await ListAsync(tenant.Hr, $"{tenant.Url}/employees?expiringSoon=true");

        var item = Assert.Single(list.GetProperty("items").EnumerateArray());
        Assert.Equal(soon.GetProperty("employee").GetProperty("id").GetGuid(), item.GetProperty("id").GetGuid());
        Assert.True(item.GetProperty("renewalNoticeDue").GetBoolean());
        Assert.Equal(1, list.GetProperty("summary").GetProperty("expiringSoon").GetInt32());
        Assert.Equal(20, soon.GetProperty("contract").GetProperty("daysUntilEnd").GetInt32());
    }

    [Fact]
    public async Task SearchMatchesNameOrDocument()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        await HireAsync(tenant, HireBody(setup, documentNumber: "55501", firstName: "Laura", lastName: "Gómez"));
        await HireAsync(tenant, HireBody(setup, documentNumber: "77702", firstName: "Pedro", lastName: "Ruiz"));

        var byName = await ListAsync(tenant.Hr, $"{tenant.Url}/employees?search=laura");
        var byDocument = await ListAsync(tenant.Hr, $"{tenant.Url}/employees?search=777");

        Assert.Equal("Laura", Assert.Single(byName.GetProperty("items").EnumerateArray()).GetProperty("firstName").GetString());
        Assert.Equal("77702", Assert.Single(byDocument.GetProperty("items").EnumerateArray()).GetProperty("documentNumber").GetString());
    }

    [Fact]
    public async Task UpdateWithoutIfMatchIs428AndWithAStaleVersionIs412()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var body = HireBody(setup);
        var detail = await HireAsync(tenant, body);
        body["lastName"] = "Gómez";

        var missing = await PutAsync(tenant.Hr, EmployeeUrl(tenant, detail), UpdateBody(body), null);
        var stale = await PutAsync(tenant.Hr, EmployeeUrl(tenant, detail), UpdateBody(body), "\"9\"");
        var fresh = await PutAsync(tenant.Hr, EmployeeUrl(tenant, detail), UpdateBody(body), Version(detail));

        Assert.Equal("precondition.if_match_required", CodeOf(await ProblemAsync(missing, HttpStatusCode.PreconditionRequired)));
        Assert.Equal("concurrency.conflict", CodeOf(await ProblemAsync(stale, HttpStatusCode.PreconditionFailed)));
        var updated = await JsonOfAsync(fresh);
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        Assert.Equal("Gómez", updated.GetProperty("employee").GetProperty("lastName").GetString());
        Assert.Equal(2, updated.GetProperty("employee").GetProperty("version").GetInt64());
    }

    // El If-Match se lee en el caso de uso, ya autorizado: quien opera en otro tenant recibe 403 y no un 428.
    [Fact]
    public async Task AnotherTenantWriteWithoutIfMatchIs403()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var body = HireBody(setup);
        var detail = await HireAsync(tenant, body);
        using var other = await NewTenantAsync(_fixture);

        var response = await PutAsync(other.Hr, EmployeeUrl(tenant, detail), UpdateBody(body), null);

        Assert.Equal("authorization.denied", CodeOf(await ProblemAsync(response, HttpStatusCode.Forbidden)));
    }

    [Fact]
    public async Task ADuplicateDocumentIs409DocumentTaken()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        await HireAsync(tenant, setup, "1001");

        var problem = await ProblemAsync(await PostEmployeeAsync(tenant, HireBody(setup, documentNumber: "1001")), HttpStatusCode.Conflict);

        Assert.Equal("hr.employee.document_taken", CodeOf(problem));
    }

    [Fact]
    public async Task AMembershipOfAnotherTenantIs422MemberInvalid()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        using var other = await NewTenantAsync(_fixture);
        var foreign = await SeedMemberAsync(_fixture.Factory, "otro@example.com", "Otra persona", "admin", tenantId: other.IdText);

        var problem = await ProblemAsync(
            await PostEmployeeAsync(tenant, HireBody(setup, memberId: foreign)), HttpStatusCode.UnprocessableEntity);

        Assert.Equal("hr.employee.member_invalid", CodeOf(problem));
    }

    [Fact]
    public async Task AnAlreadyLinkedMemberIs409MemberTaken()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var member = await SeedMemberAsync(_fixture.Factory, "vinculado@example.com", "Vinculado", "admin", tenantId: tenant.IdText);
        await HireAsync(tenant, HireBody(setup, documentNumber: "1001", memberId: member));

        var problem = await ProblemAsync(
            await PostEmployeeAsync(tenant, HireBody(setup, documentNumber: "1002", memberId: member)), HttpStatusCode.Conflict);

        Assert.Equal("hr.employee.member_taken", CodeOf(problem));
    }

    [Fact]
    public async Task AnUnknownCityIs422OnCityId()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);

        var problem = await ProblemAsync(
            await PostEmployeeAsync(tenant, HireBody(setup, cityId: Guid.NewGuid())), HttpStatusCode.UnprocessableEntity);

        Assert.True(HasError(problem, "cityId"));
    }

    [Fact]
    public async Task ThePaymentNeedsContractManageAndIfMatch()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var detail = await HireAsync(tenant, setup);
        var payment = new { paymentMethod = "BankTransfer", bankName = "Bancolombia", bankAccountType = "Savings", bankAccountNumber = "12345678901" };
        using var withoutManage = tenant.Client(HrPermissions.EmployeeRead, HrPermissions.ContractRead);

        var forbidden = await PutAsync(withoutManage, $"{EmployeeUrl(tenant, detail)}/payment", payment, Version(detail));
        var missing = await PutAsync(tenant.Hr, $"{EmployeeUrl(tenant, detail)}/payment", payment, null);
        var saved = await PutAsync(tenant.Hr, $"{EmployeeUrl(tenant, detail)}/payment", payment, Version(detail));

        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionRequired, missing.StatusCode);
        var updated = await JsonOfAsync(saved);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var shown = updated.GetProperty("employee").GetProperty("payment");
        Assert.Equal("BankTransfer", shown.GetProperty("paymentMethod").GetString());
        Assert.Equal("12345678901", shown.GetProperty("bankAccountNumber").GetString());
    }

    [Fact]
    public async Task ClearingThePaymentNullsIt()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var detail = await HireAsync(
            tenant,
            HireBody(setup, payment: new { paymentMethod = "BankTransfer", bankName = "Bancolombia", bankAccountType = "Savings", bankAccountNumber = "123" }));
        Assert.Equal("BankTransfer", detail.GetProperty("employee").GetProperty("payment").GetProperty("paymentMethod").GetString());

        var cleared = await PutAsync(
            tenant.Hr,
            $"{EmployeeUrl(tenant, detail)}/payment",
            new { paymentMethod = (string?)null, bankName = (string?)null, bankAccountType = (string?)null, bankAccountNumber = (string?)null },
            Version(detail));

        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await JsonOfAsync(cleared)).GetProperty("employee").GetProperty("payment").ValueKind);
    }

    // Review Focus 2: quien edita la ficha sin leer contratos recibe la respuesta enmascarada, igual que en el GET.
    [Fact]
    public async Task AWriteByAManagerWithoutContractReadAnswersMasked()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var body = HireBody(
            setup, payment: new { paymentMethod = "BankTransfer", bankName = "Bancolombia", bankAccountType = "Savings", bankAccountNumber = "123" });
        var detail = await HireAsync(tenant, body);
        body["lastName"] = "Gómez";
        using var manager = tenant.Client(HrPermissions.EmployeeRead, HrPermissions.EmployeeManage);

        var response = await PutAsync(manager, EmployeeUrl(tenant, detail), UpdateBody(body), Version(detail));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var masked = await JsonOfAsync(response);
        Assert.False(masked.GetProperty("canReadContract").GetBoolean());
        Assert.Equal(JsonValueKind.Null, masked.GetProperty("employee").GetProperty("payment").ValueKind);
        Assert.Equal(JsonValueKind.Null, masked.GetProperty("contract").ValueKind);
        Assert.Empty(masked.GetProperty("previousContracts").EnumerateArray());
        Assert.Equal("Gómez", masked.GetProperty("employee").GetProperty("lastName").GetString());
    }

    [Fact]
    public async Task DeactivatingClosesTheContractAndAnswersDeactivated()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var detail = await HireAsync(tenant, setup);

        var response = await tenant.Hr.PostAsJsonAsync($"{EmployeeUrl(tenant, detail)}/deactivate", new { date = "2026-11-30" }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonOfAsync(response);
        Assert.Equal("Deactivated", body.GetProperty("employee").GetProperty("status").GetString());
        var closed = body.GetProperty("previousContracts")[0];
        Assert.Equal("Closed", closed.GetProperty("status").GetString());
        Assert.Equal("2026-11-30", closed.GetProperty("endedOn").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("contract").ValueKind);
    }

    // La desactivación sólo pide hr.employee.*: quien la hace sin leer contratos recibe la ficha enmascarada, como en el GET.
    [Fact]
    public async Task ADeactivationByAManagerWithoutContractReadAnswersMasked()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var detail = await HireAsync(
            tenant,
            HireBody(setup, payment: new { paymentMethod = "BankTransfer", bankName = "Bancolombia", bankAccountType = "Savings", bankAccountNumber = "123" }));
        using var manager = tenant.Client(HrPermissions.EmployeeRead, HrPermissions.EmployeeManage);

        var response = await manager.PostAsJsonAsync($"{EmployeeUrl(tenant, detail)}/deactivate", new { date = "2026-11-30" }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var masked = await JsonOfAsync(response);
        Assert.False(masked.GetProperty("canReadContract").GetBoolean());
        Assert.Equal(JsonValueKind.Null, masked.GetProperty("employee").GetProperty("payment").ValueKind);
        Assert.Equal(JsonValueKind.Null, masked.GetProperty("contract").ValueKind);
        Assert.Empty(masked.GetProperty("previousContracts").EnumerateArray());
        Assert.Equal("Deactivated", masked.GetProperty("employee").GetProperty("status").GetString());
    }

    [Fact]
    public async Task DeactivatingBeforeTheStartIs422OnDate()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var detail = await HireAsync(tenant, setup);

        var problem = await ProblemAsync(
            await tenant.Hr.PostAsJsonAsync($"{EmployeeUrl(tenant, detail)}/deactivate", new { date = "2026-01-14" }, Ct),
            HttpStatusCode.UnprocessableEntity);

        Assert.True(HasError(problem, "date"));
    }

    [Fact]
    public async Task DeactivatingTwiceIs422NotActive()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var detail = await HireAsync(tenant, setup);
        await tenant.Hr.PostAsJsonAsync($"{EmployeeUrl(tenant, detail)}/deactivate", new { date = "2026-11-30" }, Ct);

        var problem = await ProblemAsync(
            await tenant.Hr.PostAsJsonAsync($"{EmployeeUrl(tenant, detail)}/deactivate", new { date = "2026-11-30" }, Ct),
            HttpStatusCode.UnprocessableEntity);

        Assert.Equal("hr.employee.not_active", CodeOf(problem));
    }

    [Fact]
    public async Task ANewHireCanBeDeleted()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var detail = await HireAsync(tenant, setup);

        var deleted = await tenant.Hr.DeleteAsync(EmployeeUrl(tenant, detail), Ct);
        var gone = await tenant.Hr.GetAsync(EmployeeUrl(tenant, detail), Ct);

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task ADeactivatedEmployeeIs422NotDeletable()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var detail = await HireAsync(tenant, setup);
        await tenant.Hr.PostAsJsonAsync($"{EmployeeUrl(tenant, detail)}/deactivate", new { date = "2026-11-30" }, Ct);

        var problem = await ProblemAsync(await tenant.Hr.DeleteAsync(EmployeeUrl(tenant, detail), Ct), HttpStatusCode.UnprocessableEntity);

        Assert.Equal("hr.employee.not_deletable", CodeOf(problem));
    }

    // Sólo un alta sin ningún movimiento se elimina: un segundo salario ya es un movimiento.
    [Fact]
    public async Task AnEmployeeWithASecondSalaryIs422NotDeletable()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var detail = await HireAsync(tenant, setup);
        var raise = await tenant.Hr.PostAsJsonAsync(
            $"{EmployeeUrl(tenant, detail)}/contracts/{detail.GetProperty("contract").GetProperty("id").GetGuid()}/salaries",
            new { effectiveDate = "2026-12-01", amount = 2_800_000m },
            Ct);
        Assert.Equal(HttpStatusCode.OK, raise.StatusCode);

        var shown = await GetEmployeeAsync(tenant, detail);
        var problem = await ProblemAsync(await tenant.Hr.DeleteAsync(EmployeeUrl(tenant, detail), Ct), HttpStatusCode.UnprocessableEntity);

        Assert.False(shown.GetProperty("canDelete").GetBoolean());
        Assert.Equal("hr.employee.not_deletable", CodeOf(problem));
    }

    [Fact]
    public async Task MembersAvailableListsActiveMembershipsWithoutALinkedEmployee()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var free = await SeedMemberAsync(_fixture.Factory, "libre@example.com", "Beatriz Libre", "admin", tenantId: tenant.IdText);
        var linked = await SeedMemberAsync(_fixture.Factory, "vinculada@example.com", "Ana Vinculada", "admin", tenantId: tenant.IdText);
        await SeedMemberAsync(_fixture.Factory, "quitada@example.com", "Carla Quitada", "admin", removed: true, tenantId: tenant.IdText);
        await HireAsync(tenant, HireBody(setup, memberId: linked));

        var list = await ListAsync(tenant.Hr, $"{tenant.Url}/members-available");

        var member = Assert.Single(list.EnumerateArray());
        Assert.Equal(free, member.GetProperty("memberId").GetGuid());
        Assert.Equal("Beatriz Libre", member.GetProperty("displayName").GetString());
        Assert.True(member.GetProperty("isActive").GetBoolean());
    }

    [Fact]
    public async Task TheActivityListsTheCreationWithTheActorName()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        await SeedCallerMembershipAsync(_fixture.Factory, "Laura Gómez", tenantId: tenant.IdText);
        var detail = await HireAsync(tenant, setup);

        var activity = await ListAsync(tenant.Hr, $"{EmployeeUrl(tenant, detail)}/activity");

        var entry = Assert.Single(activity.GetProperty("items").EnumerateArray());
        Assert.Equal("EmployeeCreated", entry.GetProperty("action").GetString());
        Assert.Equal("Lifecycle", entry.GetProperty("category").GetString());
        Assert.Equal("Laura Gómez", entry.GetProperty("actor").GetProperty("displayName").GetString());
        Assert.Equal(1, activity.GetProperty("total").GetInt32());
        Assert.Contains(entry.GetProperty("changes").EnumerateArray(), change => change.GetProperty("field").GetString() == "amount");
    }

    [Fact]
    public async Task EveryWriteIsAudited()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var body = HireBody(setup);
        var detail = await HireAsync(tenant, body);
        var id = detail.GetProperty("employee").GetProperty("id").GetGuid().ToString();
        body["lastName"] = "Gómez";
        await PutAsync(tenant.Hr, EmployeeUrl(tenant, detail), UpdateBody(body), Version(detail));
        await PutAsync(
            tenant.Hr,
            $"{EmployeeUrl(tenant, detail)}/payment",
            new { paymentMethod = "Other", bankName = (string?)null, bankAccountType = (string?)null, bankAccountNumber = (string?)null },
            "\"2\"");
        await tenant.Hr.PostAsJsonAsync($"{EmployeeUrl(tenant, detail)}/deactivate", new { date = "2026-11-30" }, Ct);

        foreach (var action in new[] { "created", "updated", "payment_updated", "deactivated" })
        {
            Assert.Equal(1, await CountAuditEntriesAsync(_fixture.Factory, $"hr.employee.{action}", id));
        }
    }
}
