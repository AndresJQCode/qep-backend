using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Payroll.Application;
using static Modules.Payroll.IntegrationTests.PayrollApiHarness;
using static Modules.Payroll.IntegrationTests.PayrollApiSeeds;

namespace Modules.Payroll.IntegrationTests;

/// <summary>
/// La configuración de nómina por empresa y su catálogo de conceptos. Cada prueba trabaja en su propio tenant. Las empresas se dan de
/// alta por la API de Empresas, igual que Hr.
/// </summary>
public sealed class PayrollSettingsApiTests : IClassFixture<PayrollApiFixture>
{
    private static readonly Guid CommissionsId = Guid.Parse("00000000-0000-0000-0002-000000000001");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly PayrollApiFixture _fixture;

    public PayrollSettingsApiTests(PayrollApiFixture fixture) => _fixture = fixture;

    private static object SettingsBody(
        string transportFrequency = "Biweekly",
        string transportCapBase = "SalaryOnly",
        string overtimeBase = "SalaryOnly",
        bool exonerated = true,
        bool dualControl = false) => new { transportFrequency, transportCapBase, overtimeBase, exonerated, dualControl };

    private static object Overtime(string name, decimal multiplier = 1.5m) =>
        new { kind = "Overtime", name, defaultSalaryConstitutive = (bool?)null, multiplier = (decimal?)multiplier };

    private static object Income(string name, bool? constitutive = false) =>
        new { kind = "Income", name, defaultSalaryConstitutive = constitutive, multiplier = (decimal?)null };

    private static string SettingsUrl(PayrollTenant tenant, Guid companyId) => $"{tenant.Url}/companies/{companyId}/settings";

    private static string ConceptsUrl(PayrollTenant tenant, Guid companyId) => $"{tenant.Url}/companies/{companyId}/concepts";

    private async Task<(PayrollTenant Tenant, Guid CompanyId)> NewCompanyAsync(string name = "Comercializadora QEP S.A.S.")
    {
        var tenant = await NewTenantAsync(_fixture);
        var company = await CreateCompanyAsync(tenant.Admin, tenant.Id, name);
        return (tenant, company.Id);
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await BodyAsync(response));
        return await JsonOfAsync(response);
    }

    // Decisión del owner 2026-11-21: se escriben primero.
    [Fact]
    public async Task WritesAreAudited()
    {
        var (tenant, companyId) = await NewCompanyAsync();
        using var _ = tenant;

        var saved = await PutAsync(tenant.Payroll, SettingsUrl(tenant, companyId), SettingsBody(dualControl: true), "\"0\"");
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var concept = await CreatedAsync(tenant.Payroll, ConceptsUrl(tenant, companyId), Overtime("Hora extra festiva"));
        var conceptId = concept.GetProperty("id").GetGuid().ToString();

        Assert.Equal(1, await CountAuditEntriesAsync(_fixture.Factory, PayrollAuditActions.SettingsUpdated, companyId.ToString()));
        Assert.Equal(1, await CountAuditEntriesAsync(_fixture.Factory, PayrollAuditActions.ConceptCreated, conceptId));
    }

    [Fact]
    public async Task ASettingsOnlyRoleReadsCompaniesAndSettings()
    {
        var (tenant, companyId) = await NewCompanyAsync();
        using var _ = tenant;
        using var settingsOnly = tenant.Client(PayrollPermissions.SettingsManage);

        var companies = await GetJsonAsync(settingsOnly, $"{tenant.Url}/companies");
        var settings = await GetJsonAsync(settingsOnly, SettingsUrl(tenant, companyId));

        Assert.Equal(companyId, Assert.Single(companies.EnumerateArray()).GetProperty("id").GetGuid());
        Assert.Equal(12, settings.GetProperty("concepts").GetArrayLength());
    }

    [Fact]
    public async Task ASettingsOnlyRoleWritesSettingsAndConcepts()
    {
        var (tenant, companyId) = await NewCompanyAsync();
        using var _ = tenant;
        using var settingsOnly = tenant.Client(PayrollPermissions.SettingsManage);

        var saved = await PutAsync(settingsOnly, SettingsUrl(tenant, companyId), SettingsBody(), "\"0\"");
        var created = await settingsOnly.PostAsJsonAsync(ConceptsUrl(tenant, companyId), Income("Auxilio de alimentación"), Ct);

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    }

    [Fact]
    public async Task AReadOnlyRoleReadsSettingsButCannotWriteThem()
    {
        var (tenant, companyId) = await NewCompanyAsync();
        using var _ = tenant;
        using var readOnly = tenant.Client(PayrollPermissions.PeriodRead);
        var concept = await CreatedAsync(tenant.Payroll, ConceptsUrl(tenant, companyId), Income("Bono"));
        var conceptUrl = $"{ConceptsUrl(tenant, companyId)}/{concept.GetProperty("id").GetGuid()}";

        Assert.Equal(HttpStatusCode.OK, (await readOnly.GetAsync($"{tenant.Url}/companies", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await readOnly.GetAsync(SettingsUrl(tenant, companyId), Ct)).StatusCode);

        var put = await PutAsync(readOnly, SettingsUrl(tenant, companyId), SettingsBody(), "\"0\"");
        var post = await readOnly.PostAsJsonAsync(ConceptsUrl(tenant, companyId), Income("Otro"), Ct);
        var archive = await readOnly.PostAsync($"{conceptUrl}/archive", null, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, archive.StatusCode);
    }

    [Fact]
    public async Task WithoutEitherPermissionTheSettingsAre403()
    {
        var (tenant, companyId) = await NewCompanyAsync();
        using var _ = tenant;
        using var manageOnly = tenant.Client(PayrollPermissions.PeriodManage);

        Assert.Equal(HttpStatusCode.Forbidden, (await manageOnly.GetAsync($"{tenant.Url}/companies", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manageOnly.GetAsync(SettingsUrl(tenant, companyId), Ct)).StatusCode);
    }

    [Fact]
    public async Task AnotherTenantInTheRouteIs403AuthorizationDenied()
    {
        var (tenant, companyId) = await NewCompanyAsync();
        using var _ = tenant;
        using var other = await NewTenantAsync(_fixture);

        var response = await other.Payroll.GetAsync(SettingsUrl(tenant, companyId), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("authorization.denied", CodeOf(await JsonOfAsync(response)));
    }

    [Fact]
    public async Task UnsavedSettingsAreTheDefaultsAtVersionZero()
    {
        var (tenant, companyId) = await NewCompanyAsync();
        using var _ = tenant;

        var settings = await GetJsonAsync(tenant.Payroll, SettingsUrl(tenant, companyId));

        Assert.Equal(0, settings.GetProperty("version").GetInt64());
        Assert.Equal("Biweekly", settings.GetProperty("transportFrequency").GetString());
        Assert.Equal("SalaryAndSalaryIncome", settings.GetProperty("transportCapBase").GetString());
        Assert.Equal("SalaryOnly", settings.GetProperty("overtimeBase").GetString());
        Assert.True(settings.GetProperty("exonerated").GetBoolean());
        Assert.False(settings.GetProperty("dualControl").GetBoolean());
        Assert.Equal(companyId, settings.GetProperty("company").GetProperty("id").GetGuid());
        var concepts = settings.GetProperty("concepts").EnumerateArray().ToList();
        Assert.Equal(12, concepts.Count);
        Assert.Equal(Guid.Parse("00000000-0000-0000-0001-000000000001"), concepts[0].GetProperty("id").GetGuid());
        Assert.Equal("ExtraDay", concepts[0].GetProperty("systemKey").GetString());
        Assert.Equal(JsonValueKind.Null, concepts[0].GetProperty("version").ValueKind);
        Assert.Equal(Guid.Parse("00000000-0000-0000-0003-000000000001"), concepts[11].GetProperty("id").GetGuid());
        Assert.All(concepts, concept => Assert.True(concept.GetProperty("system").GetBoolean()));
    }

    [Fact]
    public async Task TheFirstSaveUsesIfMatchZero()
    {
        var (tenant, companyId) = await NewCompanyAsync();
        using var _ = tenant;
        var url = SettingsUrl(tenant, companyId);

        var first = await PutAsync(tenant.Payroll, url, SettingsBody("Monthly", "SalaryAndSalaryIncome", "SalaryAndMonthSalaryIncome", false, true), "\"0\"");
        var again = await PutAsync(tenant.Payroll, url, SettingsBody(), "\"0\"");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var saved = await JsonOfAsync(first);
        Assert.Equal(1, saved.GetProperty("version").GetInt64());
        Assert.Equal("Monthly", saved.GetProperty("transportFrequency").GetString());
        Assert.False(saved.GetProperty("exonerated").GetBoolean());
        Assert.True(saved.GetProperty("dualControl").GetBoolean());
        Assert.Equal(HttpStatusCode.PreconditionFailed, again.StatusCode);

        var second = await PutAsync(tenant.Payroll, url, SettingsBody(dualControl: false), "\"1\"");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(2, (await JsonOfAsync(second)).GetProperty("version").GetInt64());
        Assert.Equal(2, (await GetJsonAsync(tenant.Payroll, url)).GetProperty("version").GetInt64());
    }

    [Fact]
    public async Task WithoutIfMatchIs428()
    {
        var (tenant, companyId) = await NewCompanyAsync();
        using var _ = tenant;

        var response = await PutAsync(tenant.Payroll, SettingsUrl(tenant, companyId), SettingsBody(), null);

        Assert.Equal((HttpStatusCode)428, response.StatusCode);
    }

    [Fact]
    public async Task InvalidSettingsAre422WithFieldErrors()
    {
        var (tenant, companyId) = await NewCompanyAsync();
        using var _ = tenant;

        var response = await PutAsync(
            tenant.Payroll, SettingsUrl(tenant, companyId), new { transportFrequency = "Weekly", exonerated = true }, "\"0\"");

        Assert.Equal((HttpStatusCode)422, response.StatusCode);
        var problem = await JsonOfAsync(response);
        Assert.True(HasError(problem, "transportFrequency"));
        Assert.True(HasError(problem, "dualControl"));
    }

    [Fact]
    public async Task ACustomOvertimeConceptIsListedAfterTheSystemOnes()
    {
        var (tenant, companyId) = await NewCompanyAsync();
        using var _ = tenant;

        var created = await CreatedAsync(tenant.Payroll, ConceptsUrl(tenant, companyId), Overtime("Hora extra festiva", 1.4m));
        var settings = await GetJsonAsync(tenant.Payroll, SettingsUrl(tenant, companyId));

        Assert.False(created.GetProperty("system").GetBoolean());
        Assert.Equal(1, created.GetProperty("version").GetInt64());
        Assert.Equal(1.4m, created.GetProperty("multiplier").GetDecimal());
        var concepts = settings.GetProperty("concepts").EnumerateArray().ToList();
        Assert.Equal(13, concepts.Count);
        Assert.Equal(created.GetProperty("id").GetGuid(), concepts[12].GetProperty("id").GetGuid());
        Assert.Equal("Hora extra festiva", concepts[12].GetProperty("name").GetString());
    }

    [Fact]
    public async Task ACustomNameEqualToASystemOneIsTaken()
    {
        var (tenant, companyId) = await NewCompanyAsync();
        using var _ = tenant;

        var response = await tenant.Payroll.PostAsJsonAsync(ConceptsUrl(tenant, companyId), Overtime("hora extra diurna "), Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("payroll.concept.name_taken", CodeOf(await JsonOfAsync(response)));
        // El mismo nombre en otro tipo no choca: «Comisiones» es un ingreso del sistema y esto es una deducción.
        var other = await tenant.Payroll.PostAsJsonAsync(
            ConceptsUrl(tenant, companyId), new { kind = "Deduction", name = "Comisiones", defaultSalaryConstitutive = (bool?)null, multiplier = (decimal?)null }, Ct);
        Assert.Equal(HttpStatusCode.Created, other.StatusCode);
    }

    [Fact]
    public async Task ACustomNameTakenByAnotherCustomOneIsTakenEvenWhenArchived()
    {
        var (tenant, companyId) = await NewCompanyAsync();
        using var _ = tenant;
        var first = await CreatedAsync(tenant.Payroll, ConceptsUrl(tenant, companyId), Income("Auxilio de alimentación"));
        await tenant.Payroll.PostAsync($"{ConceptsUrl(tenant, companyId)}/{first.GetProperty("id").GetGuid()}/archive", null, Ct);

        var response = await tenant.Payroll.PostAsJsonAsync(ConceptsUrl(tenant, companyId), Income("  AUXILIO de alimentacion"), Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("payroll.concept.name_taken", CodeOf(await JsonOfAsync(response)));
    }

    [Fact]
    public async Task EditingAConceptNeedsTheVersionAndKeepsItsKind()
    {
        var (tenant, companyId) = await NewCompanyAsync();
        using var _ = tenant;
        var concept = await CreatedAsync(tenant.Payroll, ConceptsUrl(tenant, companyId), Income("Bono"));
        var url = $"{ConceptsUrl(tenant, companyId)}/{concept.GetProperty("id").GetGuid()}";

        var edited = await PutAsync(tenant.Payroll, url, Income("Bono de desempeño", true), "\"1\"");
        var stale = await PutAsync(tenant.Payroll, url, Income("Otro"), "\"1\"");
        var missing = await PutAsync(tenant.Payroll, url, Income("Otro"), null);

        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        var body = await JsonOfAsync(edited);
        Assert.Equal(2, body.GetProperty("version").GetInt64());
        Assert.Equal("Bono de desempeño", body.GetProperty("name").GetString());
        Assert.True(body.GetProperty("defaultSalaryConstitutive").GetBoolean());
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal((HttpStatusCode)428, missing.StatusCode);
    }

    [Fact]
    public async Task ChangingTheKindIs422()
    {
        var (tenant, companyId) = await NewCompanyAsync();
        using var _ = tenant;
        var concept = await CreatedAsync(tenant.Payroll, ConceptsUrl(tenant, companyId), Income("Bono"));
        var url = $"{ConceptsUrl(tenant, companyId)}/{concept.GetProperty("id").GetGuid()}";

        var response = await PutAsync(
            tenant.Payroll, url, new { kind = "Deduction", name = "Bono", defaultSalaryConstitutive = (bool?)null, multiplier = (decimal?)null }, "\"1\"");

        Assert.Equal((HttpStatusCode)422, response.StatusCode);
        Assert.True(HasError(await JsonOfAsync(response), "kind"));
    }

    [Fact]
    public async Task SystemConceptsCannotBeEditedOrArchived()
    {
        var (tenant, companyId) = await NewCompanyAsync();
        using var _ = tenant;
        var url = $"{ConceptsUrl(tenant, companyId)}/{CommissionsId}";

        var edit = await PutAsync(tenant.Payroll, url, Income("Comisiones nuevas"), "\"1\"");
        var archive = await tenant.Payroll.PostAsync($"{url}/archive", null, Ct);

        Assert.Equal((HttpStatusCode)422, edit.StatusCode);
        Assert.Equal("payroll.concept.system_readonly", CodeOf(await JsonOfAsync(edit)));
        Assert.Equal((HttpStatusCode)422, archive.StatusCode);
        Assert.Equal("payroll.concept.system_readonly", CodeOf(await JsonOfAsync(archive)));
    }

    [Fact]
    public async Task ArchivingTwiceIs409()
    {
        var (tenant, companyId) = await NewCompanyAsync();
        using var _ = tenant;
        var concept = await CreatedAsync(tenant.Payroll, ConceptsUrl(tenant, companyId), Income("Bono"));
        var url = $"{ConceptsUrl(tenant, companyId)}/{concept.GetProperty("id").GetGuid()}";

        var first = await tenant.Payroll.PostAsync($"{url}/archive", null, Ct);
        var second = await tenant.Payroll.PostAsync($"{url}/archive", null, Ct);
        var notArchived = await tenant.Payroll.PostAsync($"{ConceptsUrl(tenant, companyId)}/{(await CreatedAsync(tenant.Payroll, ConceptsUrl(tenant, companyId), Income("Otro"))).GetProperty("id").GetGuid()}/unarchive", null, Ct);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.True((await JsonOfAsync(first)).GetProperty("archived").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("payroll.concept.already_archived", CodeOf(await JsonOfAsync(second)));
        Assert.Equal(HttpStatusCode.Conflict, notArchived.StatusCode);
        Assert.Equal("payroll.concept.not_archived", CodeOf(await JsonOfAsync(notArchived)));
    }

    // I-8: archivar y desarchivar son cambios de estado idempotentes, sin If-Match; editar sí lo exige.
    [Fact]
    public async Task ArchivingNeedsNoIfMatch()
    {
        var (tenant, companyId) = await NewCompanyAsync();
        using var _ = tenant;
        var concept = await CreatedAsync(tenant.Payroll, ConceptsUrl(tenant, companyId), Income("Bono"));
        var url = $"{ConceptsUrl(tenant, companyId)}/{concept.GetProperty("id").GetGuid()}";

        var archived = await tenant.Payroll.PostAsync($"{url}/archive", null, Ct);
        var unarchived = await tenant.Payroll.PostAsync($"{url}/unarchive", null, Ct);
        var edit = await PutAsync(tenant.Payroll, url, Income("Bono 2"), null);

        Assert.Equal(HttpStatusCode.OK, archived.StatusCode);
        Assert.Equal(HttpStatusCode.OK, unarchived.StatusCode);
        Assert.False((await JsonOfAsync(unarchived)).GetProperty("archived").GetBoolean());
        Assert.Equal((HttpStatusCode)428, edit.StatusCode);
    }

    [Fact]
    public async Task AConceptOfAnotherCompanyIs404()
    {
        var (tenant, companyId) = await NewCompanyAsync();
        using var _ = tenant;
        var otherCompany = await CreateCompanyAsync(tenant.Admin, tenant.Id, "Servicios del Norte S.A.S.");
        var concept = await CreatedAsync(tenant.Payroll, ConceptsUrl(tenant, companyId), Income("Bono"));

        var response = await tenant.Payroll.PostAsync(
            $"{ConceptsUrl(tenant, otherCompany.Id)}/{concept.GetProperty("id").GetGuid()}/archive", null, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("payroll.concept.not_found", CodeOf(await JsonOfAsync(response)));
    }

    [Fact]
    public async Task AnUnknownCompanyIs404()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var unknown = Guid.NewGuid();

        var settings = await tenant.Payroll.GetAsync(SettingsUrl(tenant, unknown), Ct);
        var put = await PutAsync(tenant.Payroll, SettingsUrl(tenant, unknown), SettingsBody(), "\"0\"");

        Assert.Equal(HttpStatusCode.NotFound, settings.StatusCode);
        Assert.Equal("payroll.company.not_found", CodeOf(await JsonOfAsync(settings)));
        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
    }

    [Fact]
    public async Task CompaniesListsActiveOnesByName()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var zeta = await CreateCompanyAsync(tenant.Admin, tenant.Id, "Zeta S.A.S.");
        var alfa = await CreateCompanyAsync(tenant.Admin, tenant.Id, "Alfa S.A.S.");
        var gone = await CreateCompanyAsync(tenant.Admin, tenant.Id, "Beta S.A.S.");
        await DeactivateCompanyAsync(tenant.Admin, tenant.Id, gone.Id);

        var companies = await GetJsonAsync(tenant.Payroll, $"{tenant.Url}/companies");

        Assert.Equal(
            [alfa.Id, zeta.Id],
            companies.EnumerateArray().Select(company => company.GetProperty("id").GetGuid()));
        Assert.All(companies.EnumerateArray(), company => Assert.True(company.GetProperty("isActive").GetBoolean()));
    }

    // M-1: una empresa desactivada después de configurarse sigue en la lista para poder ver su configuración.
    [Fact]
    public async Task AnInactiveCompanyWithSavedSettingsIsListed()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var company = await CreateCompanyAsync(tenant.Admin, tenant.Id, "Comercializadora QEP S.A.S.");
        var plain = await CreateCompanyAsync(tenant.Admin, tenant.Id, "Sin configuración S.A.S.");
        await PutAsync(tenant.Payroll, SettingsUrl(tenant, company.Id), SettingsBody(), "\"0\"");
        await DeactivateCompanyAsync(tenant.Admin, tenant.Id, company.Id);
        await DeactivateCompanyAsync(tenant.Admin, tenant.Id, plain.Id);

        var companies = await GetJsonAsync(tenant.Payroll, $"{tenant.Url}/companies");

        var listed = Assert.Single(companies.EnumerateArray());
        Assert.Equal(company.Id, listed.GetProperty("id").GetGuid());
        Assert.False(listed.GetProperty("isActive").GetBoolean());
    }

    // An inactive company that already has periods stays listed too: its liquidated months are still read and exported.
    [Fact]
    public async Task AnInactiveCompanyWithPeriodsIsListed()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var company = await CreateCompanyAsync(tenant.Admin, tenant.Id, "Comercializadora QEP S.A.S.");
        var plain = await CreateCompanyAsync(tenant.Admin, tenant.Id, "Sin periodos S.A.S.");
        var ensured = await tenant.Payroll.PostAsync($"{tenant.Url}/companies/{company.Id}/periods/ensure-current", null, Ct);
        Assert.Equal(System.Net.HttpStatusCode.OK, ensured.StatusCode);
        await DeactivateCompanyAsync(tenant.Admin, tenant.Id, company.Id);
        await DeactivateCompanyAsync(tenant.Admin, tenant.Id, plain.Id);

        var companies = await GetJsonAsync(tenant.Payroll, $"{tenant.Url}/companies");

        var listed = Assert.Single(companies.EnumerateArray());
        Assert.Equal(company.Id, listed.GetProperty("id").GetGuid());
        Assert.False(listed.GetProperty("isActive").GetBoolean());
    }
    // Decision 10: an inactive company's settings are read, never changed.
    [Fact]
    public async Task AnInactiveCompanyReadsButCannotSaveItsSettings()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var company = await CreateCompanyAsync(tenant.Admin, tenant.Id, "Comercializadora QEP S.A.S.");
        var first = await PutAsync(tenant.Payroll, SettingsUrl(tenant, company.Id), SettingsBody(), "\"0\"");
        Assert.Equal(System.Net.HttpStatusCode.OK, first.StatusCode);
        await DeactivateCompanyAsync(tenant.Admin, tenant.Id, company.Id);

        var saved = await PutAsync(tenant.Payroll, SettingsUrl(tenant, company.Id), SettingsBody(), "\"1\"");
        var read = await GetJsonAsync(tenant.Payroll, SettingsUrl(tenant, company.Id));

        Assert.Equal((System.Net.HttpStatusCode)422, saved.StatusCode);
        Assert.Equal("payroll.company.inactive", CodeOf(await JsonOfAsync(saved)));
        Assert.Equal(1, read.GetProperty("version").GetInt64());
        Assert.False(read.GetProperty("company").GetProperty("isActive").GetBoolean());
    }
}
