using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Hr.Application;
using Testcontainers.PostgreSql;
using static Modules.Hr.IntegrationTests.HrApiHarness;

namespace Modules.Hr.IntegrationTests;

/// <summary>
/// La configuración de la organización por HTTP (spec 2026-11-20, «Rutas»): una sola base para la clase y un tenant propio por
/// prueba, con sus empresas sembradas por la API de Companies.
/// </summary>
public sealed class HrSettingsApiTests : IClassFixture<HrSettingsApiTests.World>
{
    // Los dos últimos dígitos del id deben ser distintos: el slug del tenant sale de ellos.
    private static int _tenantCounter = 0x10;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly World _world;

    public HrSettingsApiTests(World world) => _world = world;

    public sealed class World : IAsyncLifetime
    {
        private PostgreSqlContainer? _database;

        internal QepApiFactory Factory { get; private set; } = null!;

        public async ValueTask InitializeAsync()
        {
            _database = await StartDatabaseAsync();
            Factory = new QepApiFactory(_database.GetConnectionString());
        }

        public async ValueTask DisposeAsync()
        {
            Factory?.Dispose();
            if (_database is not null)
            {
                await _database.DisposeAsync();
            }
        }
    }

    private sealed class Tenant(string idText, HttpClient hr, HttpClient admin) : IDisposable
    {
        public string IdText { get; } = idText;

        public Guid Id { get; } = Guid.Parse(idText);

        /// <summary>Los cinco permisos de Hr.</summary>
        public HttpClient Hr { get; } = hr;

        /// <summary>Empresas: lo que hace falta para sembrar y desactivar.</summary>
        public HttpClient Admin { get; } = admin;

        public string Url => HrUrl(IdText);

        public void Dispose()
        {
            Hr.Dispose();
            Admin.Dispose();
        }
    }

    private async Task<Tenant> NewTenantAsync()
    {
        var idText = $"01900000-0000-7000-8000-0000000000{Interlocked.Increment(ref _tenantCounter):x2}";
        await SeedTenantAsync(_world.Factory, idText);
        var hr = CreateClient(
            _world.Factory, SubjectId, idText,
            HrPermissions.EmployeeRead, HrPermissions.EmployeeManage, HrPermissions.ContractRead, HrPermissions.ContractManage,
            HrPermissions.SettingsManage);
        var admin = CreateClient(_world.Factory, SubjectId, idText, "companies.company.read", "companies.company.manage");
        return new Tenant(idText, hr, admin);
    }

    private static Task<CompanyRef> CompanyAsync(Tenant tenant, string name = "Comercializadora QEP S.A.S.") =>
        CreateCompanyAsync(tenant.Admin, tenant.Id, name);

    private static async Task<JsonElement> JsonOfAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await BodyAsync(response));
        return document.RootElement.Clone();
    }

    private static async Task<JsonElement> CreatedAsync(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await BodyAsync(response));
        return await JsonOfAsync(response);
    }

    private static Task<JsonElement> SiteAsync(Tenant tenant, Guid companyId, string name = "Principal") =>
        CreatedAsync(tenant.Hr, $"{tenant.Url}/sites", new { companyId, name, address = (string?)null, cityId = (Guid?)null, compensationFund = (string?)null });

    private static Task<JsonElement> WorkplaceAsync(Tenant tenant, Guid siteId, string name = "Bodega") =>
        CreatedAsync(tenant.Hr, $"{tenant.Url}/workplaces", new { name, siteId, ciiuCode = "4711", riskClass = "III", highRiskPension = false });

    private static Task<JsonElement> NamedAsync(Tenant tenant, string segment, string name) =>
        CreatedAsync(tenant.Hr, $"{tenant.Url}/{segment}", new { name });

    private static async Task<HttpResponseMessage> PutAsync(HttpClient client, string url, object body, string? ifMatch)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = JsonContent.Create(body) };
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return await client.SendAsync(request, Ct);
    }

    private static async Task<JsonElement> SettingsAsync(Tenant tenant)
    {
        var response = await tenant.Hr.GetAsync($"{tenant.Url}/settings", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await JsonOfAsync(response);
    }

    private static string CodeOf(JsonElement problem) => problem.GetProperty("code").GetString()!;

    private static string[] ErrorKeys(JsonElement problem) =>
        problem.GetProperty("errors").EnumerateObject().Select(property => property.Name).ToArray();

    [Fact]
    public async Task CreatesTheFourAndListsThemInOneCall()
    {
        using var tenant = await NewTenantAsync();
        var company = await CompanyAsync(tenant);
        var site = await SiteAsync(tenant, company.Id);
        var workplace = await WorkplaceAsync(tenant, site.GetProperty("id").GetGuid());
        await NamedAsync(tenant, "areas", "Logística");
        await NamedAsync(tenant, "positions", "Analista");

        var settings = await SettingsAsync(tenant);

        Assert.Single(settings.GetProperty("sites").EnumerateArray());
        Assert.Single(settings.GetProperty("workplaces").EnumerateArray());
        Assert.Single(settings.GetProperty("areas").EnumerateArray());
        Assert.Single(settings.GetProperty("positions").EnumerateArray());
        Assert.True(settings.GetProperty("setupComplete").GetBoolean());
        var listed = settings.GetProperty("workplaces")[0];
        Assert.Equal(workplace.GetProperty("id").GetGuid(), listed.GetProperty("id").GetGuid());
        Assert.Equal(site.GetProperty("id").GetGuid(), listed.GetProperty("site").GetProperty("id").GetGuid());
        Assert.Equal("III", listed.GetProperty("riskClass").GetString());
        Assert.Equal("4711", listed.GetProperty("ciiuCode").GetString());
        Assert.False(listed.GetProperty("inUse").GetBoolean());
        Assert.Equal(1, listed.GetProperty("version").GetInt64());
    }

    [Fact]
    public async Task WithoutPositionSetupIsIncomplete()
    {
        using var tenant = await NewTenantAsync();
        var company = await CompanyAsync(tenant);
        var site = await SiteAsync(tenant, company.Id);
        await WorkplaceAsync(tenant, site.GetProperty("id").GetGuid());

        var settings = await SettingsAsync(tenant);

        Assert.False(settings.GetProperty("setupComplete").GetBoolean());
    }

    [Fact]
    public async Task ADuplicateNameIgnoringCaseAndAccentsIs409()
    {
        using var tenant = await NewTenantAsync();
        await NamedAsync(tenant, "areas", "Logística");

        var response = await tenant.Hr.PostAsJsonAsync($"{tenant.Url}/areas", new { name = "LOGISTICA" }, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("hr.settings.name_taken", CodeOf(await JsonOfAsync(response)));
    }

    [Fact]
    public async Task TheSameWorkplaceNameInAnotherSiteIsAllowed()
    {
        using var tenant = await NewTenantAsync();
        var company = await CompanyAsync(tenant);
        var first = await SiteAsync(tenant, company.Id, "Principal");
        var second = await SiteAsync(tenant, company.Id, "Norte");
        await WorkplaceAsync(tenant, first.GetProperty("id").GetGuid());

        await WorkplaceAsync(tenant, second.GetProperty("id").GetGuid());

        var duplicate = await tenant.Hr.PostAsJsonAsync(
            $"{tenant.Url}/workplaces",
            new { name = " bodega ", siteId = first.GetProperty("id").GetGuid(), ciiuCode = (string?)null, riskClass = "I", highRiskPension = (bool?)null },
            Ct);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    }

    [Fact]
    public async Task UpdateNeedsIfMatch()
    {
        using var tenant = await NewTenantAsync();
        var area = await NamedAsync(tenant, "areas", "Logística");
        var url = $"{tenant.Url}/areas/{area.GetProperty("id").GetGuid()}";

        var missing = await PutAsync(tenant.Hr, url, new { name = "Logística 2" }, null);
        var stale = await PutAsync(tenant.Hr, url, new { name = "Logística 2" }, "\"99\"");
        var current = await PutAsync(tenant.Hr, url, new { name = "Logística 2" }, "\"1\"");

        Assert.Equal((HttpStatusCode)428, missing.StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        var updated = await JsonOfAsync(current);
        Assert.Equal("Logística 2", updated.GetProperty("name").GetString());
        Assert.Equal(2, updated.GetProperty("version").GetInt64());
    }

    [Fact]
    public async Task AWorkplaceCannotChangeSite()
    {
        using var tenant = await NewTenantAsync();
        var company = await CompanyAsync(tenant);
        var first = await SiteAsync(tenant, company.Id, "Principal");
        var second = await SiteAsync(tenant, company.Id, "Norte");
        var workplace = await WorkplaceAsync(tenant, first.GetProperty("id").GetGuid());

        var response = await PutAsync(
            tenant.Hr,
            $"{tenant.Url}/workplaces/{workplace.GetProperty("id").GetGuid()}",
            new { name = "Bodega", siteId = second.GetProperty("id").GetGuid(), ciiuCode = "4711", riskClass = "III", highRiskPension = false },
            "\"1\"");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("siteId", ErrorKeys(await JsonOfAsync(response)));
    }

    [Fact]
    public async Task ArchivingASiteWithAnActiveWorkplaceIs422()
    {
        using var tenant = await NewTenantAsync();
        var company = await CompanyAsync(tenant);
        var site = await SiteAsync(tenant, company.Id);
        await WorkplaceAsync(tenant, site.GetProperty("id").GetGuid());

        var response = await tenant.Hr.PostAsync($"{tenant.Url}/sites/{site.GetProperty("id").GetGuid()}/archive", null, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("hr.settings.site_has_active_workplaces", CodeOf(await JsonOfAsync(response)));
    }

    [Fact]
    public async Task AWorkplaceOnAnArchivedSiteIs422SiteArchived()
    {
        using var tenant = await NewTenantAsync();
        var company = await CompanyAsync(tenant);
        var site = await SiteAsync(tenant, company.Id);
        var siteId = site.GetProperty("id").GetGuid();
        var archived = await tenant.Hr.PostAsync($"{tenant.Url}/sites/{siteId}/archive", null, Ct);
        Assert.Equal(HttpStatusCode.OK, archived.StatusCode);

        var response = await tenant.Hr.PostAsJsonAsync(
            $"{tenant.Url}/workplaces", new { name = "Bodega", siteId, ciiuCode = (string?)null, riskClass = "I", highRiskPension = (bool?)null }, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("hr.settings.site_archived", CodeOf(await JsonOfAsync(response)));
    }

    [Fact]
    public async Task ArchivedSettingsStayInTheListMarked()
    {
        using var tenant = await NewTenantAsync();
        var position = await NamedAsync(tenant, "positions", "Analista");
        var id = position.GetProperty("id").GetGuid();

        var archive = await tenant.Hr.PostAsync($"{tenant.Url}/positions/{id}/archive", null, Ct);
        var again = await tenant.Hr.PostAsync($"{tenant.Url}/positions/{id}/archive", null, Ct);

        Assert.Equal(HttpStatusCode.OK, archive.StatusCode);
        Assert.True((await JsonOfAsync(archive)).GetProperty("archived").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("hr.settings.already_archived", CodeOf(await JsonOfAsync(again)));
        var listed = (await SettingsAsync(tenant)).GetProperty("positions")[0];
        Assert.True(listed.GetProperty("archived").GetBoolean());

        var unarchive = await tenant.Hr.PostAsync($"{tenant.Url}/positions/{id}/unarchive", null, Ct);
        var unarchiveAgain = await tenant.Hr.PostAsync($"{tenant.Url}/positions/{id}/unarchive", null, Ct);
        Assert.False((await JsonOfAsync(unarchive)).GetProperty("archived").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, unarchiveAgain.StatusCode);
        Assert.Equal("hr.settings.not_archived", CodeOf(await JsonOfAsync(unarchiveAgain)));
    }

    [Fact]
    public async Task DeletingAnUnusedSettingIs204()
    {
        using var tenant = await NewTenantAsync();
        var area = await NamedAsync(tenant, "areas", "Logística");

        var response = await tenant.Hr.DeleteAsync($"{tenant.Url}/areas/{area.GetProperty("id").GetGuid()}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty((await SettingsAsync(tenant)).GetProperty("areas").EnumerateArray());
    }

    [Fact]
    public async Task DeletingASiteWithAWorkplaceIs409InUse()
    {
        using var tenant = await NewTenantAsync();
        var company = await CompanyAsync(tenant);
        var site = await SiteAsync(tenant, company.Id);
        await WorkplaceAsync(tenant, site.GetProperty("id").GetGuid());

        var response = await tenant.Hr.DeleteAsync($"{tenant.Url}/sites/{site.GetProperty("id").GetGuid()}", Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("hr.settings.in_use", CodeOf(await JsonOfAsync(response)));
        Assert.True((await SettingsAsync(tenant)).GetProperty("sites")[0].GetProperty("inUse").GetBoolean());
    }

    [Fact]
    public async Task AnUnknownSettingIs404()
    {
        using var tenant = await NewTenantAsync();

        var site = await tenant.Hr.PostAsync($"{tenant.Url}/sites/{Guid.NewGuid()}/archive", null, Ct);
        var area = await tenant.Hr.DeleteAsync($"{tenant.Url}/areas/{Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, site.StatusCode);
        Assert.Equal("hr.site.not_found", CodeOf(await JsonOfAsync(site)));
        Assert.Equal(HttpStatusCode.NotFound, area.StatusCode);
    }

    [Fact]
    public async Task WithoutSettingsManageWritesAre403()
    {
        using var tenant = await NewTenantAsync();
        using var reader = CreateClient(_world.Factory, SubjectId, tenant.IdText, HrPermissions.EmployeeRead);

        var response = await reader.PostAsJsonAsync($"{tenant.Url}/areas", new { name = "Logística" }, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // D-1: cada `manage` exige también su `read`. La política deja pasar a quien tiene `hr.settings.manage`; el handler lo frena.
    [Fact]
    public async Task WithOnlySettingsManageWritesAre403()
    {
        using var tenant = await NewTenantAsync();
        using var writer = CreateClient(_world.Factory, SubjectId, tenant.IdText, HrPermissions.SettingsManage);

        var response = await writer.PostAsJsonAsync($"{tenant.Url}/areas", new { name = "Logística" }, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("authorization.denied", CodeOf(await JsonOfAsync(response)));
    }

    [Fact]
    public async Task AnotherTenantInTheRouteIs403()
    {
        using var tenant = await NewTenantAsync();
        using var other = await NewTenantAsync();

        var response = await other.Hr.GetAsync($"{tenant.Url}/settings", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // El If-Match se lee en el caso de uso, ya autorizado: quien opera en otro tenant recibe 403 aunque no mande el encabezado (antes
    // el endpoint lo exigía primero y respondía 428).
    [Fact]
    public async Task AnotherTenantWriteWithoutIfMatchIs403()
    {
        using var tenant = await NewTenantAsync();
        using var other = await NewTenantAsync();
        var area = await NamedAsync(tenant, "areas", "Logística");

        var response = await PutAsync(other.Hr, $"{tenant.Url}/areas/{area.GetProperty("id").GetGuid()}", new { name = "Otra" }, null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("authorization.denied", CodeOf(await JsonOfAsync(response)));
    }

    [Fact]
    public async Task EveryWriteIsAudited()
    {
        using var tenant = await NewTenantAsync();
        var area = await NamedAsync(tenant, "areas", "Logística");
        var id = area.GetProperty("id").GetGuid().ToString();
        await PutAsync(tenant.Hr, $"{tenant.Url}/areas/{id}", new { name = "Logística 2" }, "\"1\"");
        await tenant.Hr.PostAsync($"{tenant.Url}/areas/{id}/archive", null, Ct);
        await tenant.Hr.PostAsync($"{tenant.Url}/areas/{id}/unarchive", null, Ct);
        await tenant.Hr.DeleteAsync($"{tenant.Url}/areas/{id}", Ct);

        Assert.Equal(1, await CountAuditEntriesAsync(_world.Factory, "hr.settings.created", id));
        Assert.Equal(1, await CountAuditEntriesAsync(_world.Factory, "hr.settings.updated", id));
        Assert.Equal(1, await CountAuditEntriesAsync(_world.Factory, "hr.settings.archived", id));
        Assert.Equal(1, await CountAuditEntriesAsync(_world.Factory, "hr.settings.unarchived", id));
        Assert.Equal(1, await CountAuditEntriesAsync(_world.Factory, "hr.settings.deleted", id));
    }

    [Fact]
    public async Task TheSettingsListTheFixedCompensationFunds()
    {
        using var tenant = await NewTenantAsync();

        var funds = (await SettingsAsync(tenant)).GetProperty("compensationFunds").EnumerateArray().Select(fund => fund.GetString()!).ToArray();

        Assert.Equal(42, funds.Length);
        Assert.Equal(funds.Length, funds.Distinct().Count());
        Assert.Contains("COMFAMA", funds);
    }

    [Fact]
    public async Task ASiteAcceptsACatalogFundAndNone()
    {
        using var tenant = await NewTenantAsync();
        var company = await CompanyAsync(tenant);

        var withFund = await CreatedAsync(
            tenant.Hr, $"{tenant.Url}/sites",
            new { companyId = company.Id, name = "Medellín", address = (string?)null, cityId = (Guid?)null, compensationFund = "COMFAMA" });
        var without = await SiteAsync(tenant, company.Id, "Bogotá");

        Assert.Equal("COMFAMA", withFund.GetProperty("compensationFund").GetString());
        Assert.Equal(JsonValueKind.Null, without.GetProperty("compensationFund").ValueKind);
    }

    [Fact]
    public async Task ASiteRejectsAFreeTextFundWith422()
    {
        using var tenant = await NewTenantAsync();
        var company = await CompanyAsync(tenant);
        var site = await SiteAsync(tenant, company.Id);

        var create = await tenant.Hr.PostAsJsonAsync(
            $"{tenant.Url}/sites",
            new { companyId = company.Id, name = "Cali", address = (string?)null, cityId = (Guid?)null, compensationFund = "Caja inventada" },
            Ct);
        var update = await PutAsync(
            tenant.Hr, $"{tenant.Url}/sites/{site.GetProperty("id").GetGuid()}",
            new { companyId = company.Id, name = "Principal", address = (string?)null, cityId = (Guid?)null, compensationFund = "comfama" },
            "\"1\"");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, create.StatusCode);
        Assert.Equal(["compensationFund"], ErrorKeys(await JsonOfAsync(create)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, update.StatusCode);
        Assert.Equal(["compensationFund"], ErrorKeys(await JsonOfAsync(update)));
    }

    [Fact]
    public async Task EveryCompanyInTheSettingsCarriesItsCountry()
    {
        using var tenant = await NewTenantAsync();
        await CompanyAsync(tenant, "Beta S.A.S.");
        await CreateForeignCompanyAsync(tenant.Admin, tenant.Id, "Andes Inc.");

        var companies = (await SettingsAsync(tenant)).GetProperty("companies").EnumerateArray().ToArray();

        Assert.Equal(
            [("Andes Inc.", "US"), ("Beta S.A.S.", "CO")],
            companies.Select(company => (company.GetProperty("name").GetString()!, company.GetProperty("country").GetString()!)));
    }

    [Fact]
    public async Task ASiteOfAForeignCompanyHasNoFund()
    {
        using var tenant = await NewTenantAsync();
        var foreign = await CreateForeignCompanyAsync(tenant.Admin, tenant.Id);

        var rejected = await tenant.Hr.PostAsJsonAsync(
            $"{tenant.Url}/sites",
            new { companyId = foreign.Id, name = "Miami", address = (string?)null, cityId = (Guid?)null, compensationFund = "COMFAMA" },
            Ct);
        var created = await SiteAsync(tenant, foreign.Id, "Miami");
        var update = await PutAsync(
            tenant.Hr, $"{tenant.Url}/sites/{created.GetProperty("id").GetGuid()}",
            new { companyId = foreign.Id, name = "Miami", address = (string?)null, cityId = (Guid?)null, compensationFund = "COMFAMA" },
            "\"1\"");
        var cleared = await PutAsync(
            tenant.Hr, $"{tenant.Url}/sites/{created.GetProperty("id").GetGuid()}",
            new { companyId = foreign.Id, name = "Miami", address = (string?)null, cityId = (Guid?)null, compensationFund = (string?)null },
            "\"1\"");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);
        var problem = await JsonOfAsync(rejected);
        Assert.Equal(["compensationFund"], ErrorKeys(problem));
        Assert.Equal(
            "La caja de compensación sólo aplica a empresas de Colombia.",
            problem.GetProperty("errors").GetProperty("compensationFund")[0].GetString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, update.StatusCode);
        Assert.Equal(["compensationFund"], ErrorKeys(await JsonOfAsync(update)));
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
    }

    [Fact]
    public async Task ACityThatDoesNotExistIs422()
    {
        using var tenant = await NewTenantAsync();
        var company = await CompanyAsync(tenant);

        var response = await tenant.Hr.PostAsJsonAsync(
            $"{tenant.Url}/sites",
            new { companyId = company.Id, name = "Principal", address = (string?)null, cityId = Guid.NewGuid(), compensationFund = (string?)null },
            Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("cityId", ErrorKeys(await JsonOfAsync(response)));
    }

    [Fact]
    public async Task TheSettingsListOnlyActiveCompaniesByName()
    {
        using var tenant = await NewTenantAsync();
        var beta = await CompanyAsync(tenant, "Beta S.A.S.");
        await CompanyAsync(tenant, "Alfa S.A.S.");
        var gamma = await CompanyAsync(tenant, "Gamma S.A.S.");
        await DeactivateCompanyAsync(tenant.Admin, tenant.Id, gamma.Id);

        var companies = (await SettingsAsync(tenant)).GetProperty("companies").EnumerateArray().ToArray();

        Assert.Equal(["Alfa S.A.S.", "Beta S.A.S."], companies.Select(company => company.GetProperty("name").GetString()));
        Assert.All(companies, company => Assert.True(company.GetProperty("isActive").GetBoolean()));
        Assert.Equal(beta.TaxId, companies[1].GetProperty("taxId").GetString());
    }

    [Fact]
    public async Task ASiteAnswersItsCompany()
    {
        using var tenant = await NewTenantAsync();
        var company = await CompanyAsync(tenant, "Alfa S.A.S.");

        var site = await SiteAsync(tenant, company.Id);

        var answered = site.GetProperty("company");
        Assert.Equal(company.Id, answered.GetProperty("id").GetGuid());
        Assert.Equal("Alfa S.A.S.", answered.GetProperty("name").GetString());
        Assert.Equal(company.TaxId, answered.GetProperty("taxId").GetString());
        Assert.True(answered.GetProperty("isActive").GetBoolean());
        Assert.Equal(JsonValueKind.Null, site.GetProperty("city").ValueKind);
    }

    [Fact]
    public async Task ASiteNeedsAnActiveCompany()
    {
        using var tenant = await NewTenantAsync();
        using var foreign = await NewTenantAsync();
        var foreignCompany = await CompanyAsync(foreign);
        var deactivated = await CompanyAsync(tenant, "Inactiva S.A.S.");
        await DeactivateCompanyAsync(tenant.Admin, tenant.Id, deactivated.Id);

        foreach (var companyId in new Guid?[] { null, Guid.NewGuid(), foreignCompany.Id, deactivated.Id })
        {
            var response = await tenant.Hr.PostAsJsonAsync(
                $"{tenant.Url}/sites",
                new { companyId, name = "Principal", address = (string?)null, cityId = (Guid?)null, compensationFund = (string?)null },
                Ct);

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Contains("companyId", ErrorKeys(await JsonOfAsync(response)));
        }
    }

    [Fact]
    public async Task ASiteCannotChangeCompany()
    {
        using var tenant = await NewTenantAsync();
        var company = await CompanyAsync(tenant, "Alfa S.A.S.");
        var other = await CompanyAsync(tenant, "Beta S.A.S.");
        var site = await SiteAsync(tenant, company.Id);

        var response = await PutAsync(
            tenant.Hr,
            $"{tenant.Url}/sites/{site.GetProperty("id").GetGuid()}",
            new { companyId = other.Id, name = "Principal", address = (string?)null, cityId = (Guid?)null, compensationFund = (string?)null },
            "\"1\"");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("companyId", ErrorKeys(await JsonOfAsync(response)));
    }

    [Fact]
    public async Task TheSameSiteNameInAnotherCompanyIsAllowed()
    {
        using var tenant = await NewTenantAsync();
        var first = await CompanyAsync(tenant, "Alfa S.A.S.");
        var second = await CompanyAsync(tenant, "Beta S.A.S.");

        await SiteAsync(tenant, first.Id, "Principal");
        await SiteAsync(tenant, second.Id, "Principal");

        Assert.Equal(2, (await SettingsAsync(tenant)).GetProperty("sites").GetArrayLength());
    }

    [Fact]
    public async Task ADuplicateSiteNameInTheSameCompanyIs409()
    {
        using var tenant = await NewTenantAsync();
        var company = await CompanyAsync(tenant);
        await SiteAsync(tenant, company.Id, "Principal");

        var response = await tenant.Hr.PostAsJsonAsync(
            $"{tenant.Url}/sites",
            new { companyId = company.Id, name = " PRINCIPAL ", address = (string?)null, cityId = (Guid?)null, compensationFund = (string?)null },
            Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("hr.settings.name_taken", CodeOf(await JsonOfAsync(response)));
    }

    [Fact]
    public async Task SetupNeedsAnActiveCompanyWithASiteAndAWorkplace()
    {
        using var tenant = await NewTenantAsync();
        var company = await CompanyAsync(tenant);
        var site = await SiteAsync(tenant, company.Id);
        await WorkplaceAsync(tenant, site.GetProperty("id").GetGuid());
        await NamedAsync(tenant, "positions", "Analista");
        Assert.True((await SettingsAsync(tenant)).GetProperty("setupComplete").GetBoolean());

        await DeactivateCompanyAsync(tenant.Admin, tenant.Id, company.Id);

        Assert.False((await SettingsAsync(tenant)).GetProperty("setupComplete").GetBoolean());
    }

    [Fact]
    public async Task ADeactivatedCompanyKeepsItsSites()
    {
        using var tenant = await NewTenantAsync();
        var company = await CompanyAsync(tenant);
        var site = await SiteAsync(tenant, company.Id);
        var siteId = site.GetProperty("id").GetGuid();
        await DeactivateCompanyAsync(tenant.Admin, tenant.Id, company.Id);

        var listed = (await SettingsAsync(tenant)).GetProperty("sites")[0];
        Assert.False(listed.GetProperty("company").GetProperty("isActive").GetBoolean());

        // D-12: editar la sede y crearle un centro siguen funcionando con la empresa desactivada.
        var edited = await PutAsync(
            tenant.Hr,
            $"{tenant.Url}/sites/{siteId}",
            new { companyId = company.Id, name = "Principal 2", address = (string?)null, cityId = (Guid?)null, compensationFund = (string?)null },
            "\"1\"");
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        await WorkplaceAsync(tenant, siteId);
    }

    // D-11: la FK de la base impide borrar una empresa con RRHH, y Companies ya traduce el SQLSTATE a 422.
    [Fact]
    public async Task DeletingACompanyWithOnlyASiteIs422InUse()
    {
        using var tenant = await NewTenantAsync();
        var company = await CompanyAsync(tenant);
        var site = await SiteAsync(tenant, company.Id);
        var companyUrl = $"/api/v1/tenants/{tenant.IdText}/companies/{company.Id}";

        var blocked = await tenant.Admin.DeleteAsync(companyUrl, Ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, blocked.StatusCode);
        Assert.Equal("companies.company.in_use", CodeOf(await JsonOfAsync(blocked)));

        var removedSite = await tenant.Hr.DeleteAsync($"{tenant.Url}/sites/{site.GetProperty("id").GetGuid()}", Ct);
        Assert.Equal(HttpStatusCode.NoContent, removedSite.StatusCode);
        var deleted = await tenant.Admin.DeleteAsync(companyUrl, Ct);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
    }

    [Fact]
    public async Task ReadingSettingsNeedsNoCompaniesPermission()
    {
        using var tenant = await NewTenantAsync();
        await CompanyAsync(tenant);
        using var reader = CreateClient(_world.Factory, SubjectId, tenant.IdText, HrPermissions.EmployeeRead);

        var response = await reader.GetAsync($"{tenant.Url}/settings", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single((await JsonOfAsync(response)).GetProperty("companies").EnumerateArray());
    }
}
