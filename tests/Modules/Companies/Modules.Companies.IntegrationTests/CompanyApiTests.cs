using System.Net;
using System.Net.Http.Json;
using Modules.Companies.Application;
using static Modules.Companies.IntegrationTests.CompaniesApiHarness;

namespace Modules.Companies.IntegrationTests;

public sealed class CompanyApiTests
{
    [Fact]
    public async Task ListReturnsAnEmptyResultForANewTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(
            factory, SubjectId, TenantId, CompaniesPermissions.CompanyRead);

        var response = await client.GetAsync(
            CompaniesUrl(), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CompaniesResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        Assert.Empty(body.Items);
    }

    // El handler revalida el tenant activo del llamador contra el de la ruta antes de tocar el
    // repositorio, asi que esto es 403 y no 404 — un 404 filtraria si el otro tenant esta vacio.
    [Fact]
    public async Task ListForAnotherTenantIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        // Tiene el permiso: el 403 tiene que venir del tenant que no coincide, no de un permiso
        // faltante, o la prueba sobreviviria a que se quite el aislamiento de tenant.
        using var client = CreateClient(
            factory, OtherSubjectId, OtherTenantId, CompaniesPermissions.CompanyRead);

        var response = await client.GetAsync(
            CompaniesUrl(), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ListWithoutTheReadPermissionIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, "tenancy.settings.read");

        var response = await client.GetAsync(
            CompaniesUrl(), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetAnUnknownCompanyIsNotFound()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(
            factory, SubjectId, TenantId, CompaniesPermissions.CompanyRead);

        var response = await client.GetAsync(
            $"{CompaniesUrl()}/{Guid.CreateVersion7()}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        // El codigo literal que el consumidor ya emite en sus fixtures. Cambiarlo rompe el
        // frontend en silencio.
        Assert.Contains("companies.company.not_found", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListMatchesByNameAndByAccountNumber()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        await CreateCompanyAsync(client, "Andes Logistica S.A.S.", "CTA-000123");
        await CreateCompanyAsync(client, "Textiles Andinos S.A.S.", "CTA-000456");

        var byName = await ListAsync(client, "?search=logistica");
        var byAccount = await ListAsync(client, "?search=000456");

        Assert.Equal("Andes Logistica S.A.S.", Assert.Single(byName.Items).Name);
        Assert.Equal(
            "CTA-000456",
            Assert.Single(Assert.Single(byAccount.Items).AccountNumbers));
    }

    // Dos cajas separadas (CLI-FILTROS-01): `name` filtra solo por nombre, `taxId` solo por
    // NIT — a diferencia de `search`, que combina nombre y numero de cuenta con OR.
    [Fact]
    public async Task ListFiltersByNameAndByTaxIdIndependently()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        await CreateCompanyAsync(
            client, "Andes Logistica S.A.S.", "CTA-000123", taxId: "900.111.111-1");
        await CreateCompanyAsync(
            client, "Textiles Andinos S.A.S.", "CTA-000456", taxId: "900.222.222-2");

        var byName = await ListAsync(client, "?name=logistica");
        var byTaxId = await ListAsync(client, "?taxId=900.222");

        Assert.Equal("Andes Logistica S.A.S.", Assert.Single(byName.Items).Name);
        Assert.Equal("Textiles Andinos S.A.S.", Assert.Single(byTaxId.Items).Name);
    }

    // Se combinan con AND cuando se llenan las dos, igual que las tres cajas de clientes.
    [Fact]
    public async Task ListCombinesNameAndTaxIdWithAnd()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        await CreateCompanyAsync(
            client, "Andes Logistica S.A.S.", "CTA-000123", taxId: "900.111.111-1");
        await CreateCompanyAsync(
            client, "Andes Textiles S.A.S.", "CTA-000456", taxId: "900.222.222-2");

        var both = await ListAsync(client, "?name=andes&taxId=900.111");
        // Las dos empresas coinciden por nombre ("andes"), pero ningun NIT coincide con este
        // — si el AND no se aplicara, esto devolveria alguna igual.
        var neither = await ListAsync(client, "?name=andes&taxId=999.999");

        Assert.Equal("Andes Logistica S.A.S.", Assert.Single(both.Items).Name);
        Assert.Empty(neither.Items);
    }

    // `%` y `_` son comodines de LIKE: sin escaparlos, `?search=_` devuelve el listado entero
    // —coincide con cualquier caracter—, que es lo contrario de filtrar. Es el defecto que la
    // revision de fiabilidad de CAT-02 encontro en este mismo codigo.
    [Fact]
    public async Task ListEscapesTheLikeWildcards()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        await CreateCompanyAsync(client, "Andes Logistica S.A.S.", "CTA-000123");
        await CreateCompanyAsync(client, "Textiles Andinos S.A.S.", "CTA-000456");

        var underscore = await ListAsync(client, "?search=_");
        var percent = await ListAsync(client, "?search=%25");

        Assert.Empty(underscore.Items);
        Assert.Empty(percent.Items);
    }

    [Fact]
    public async Task ListFiltersByStatusAndReturnsBothWhenTheFilterIsAbsent()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        await CreateCompanyAsync(client, "Andes Logistica S.A.S.", "CTA-000123");
        var retired = await CreateCompanyAsync(client, "Textiles Andinos", "CTA-000456");
        var deactivated = await client.PostAsync(
            $"{CompaniesUrl()}/{retired.Id}/deactivate",
            content: null,
            TestContext.Current.CancellationToken);
        deactivated.EnsureSuccessStatusCode();

        var active = await ListAsync(client, "?status=active");
        var inactive = await ListAsync(client, "?status=inactive");
        var all = await ListAsync(client, string.Empty);

        Assert.Equal(
            "CTA-000123",
            Assert.Single(Assert.Single(active.Items).AccountNumbers));
        Assert.Equal(
            "CTA-000456",
            Assert.Single(Assert.Single(inactive.Items).AccountNumbers));
        Assert.Equal(2, all.Items.Count);
    }

    // Un filtro que no se reconoce falla, no se ignora: devolver el listado completo con un 200
    // le hace concluir a quien escribio `?status=activo` que no hay empresas inactivas.
    [Fact]
    public async Task ListRejectsAnUnknownStatusFilter()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(
            factory, SubjectId, TenantId, CompaniesPermissions.CompanyRead);

        var response = await client.GetAsync(
            $"{CompaniesUrl()}?status=activo", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        Assert.Contains(
            "companies.company.status_filter_invalid", body, StringComparison.Ordinal);
    }

    // La otra mitad del aislamiento: no alcanza con que el listado ajeno responda 403, el propio
    // no tiene que traer nada del vecino.
    [Fact]
    public async Task ListReturnsOnlyTheCallersOwnCompanies()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var mine = CreateManager(factory);
        using var theirs = CreateClient(
            factory,
            OtherSubjectId,
            OtherTenantId,
            CompaniesPermissions.CompanyRead,
            CompaniesPermissions.CompanyManage);
        await CreateCompanyAsync(mine, "Andes Logistica S.A.S.", "CTA-000123");
        await CreateCompanyAsync(
            theirs, "Empresa Ajena S.A.S.", "CTA-000123", tenantId: OtherTenantId);

        var body = await ListAsync(mine, string.Empty);

        Assert.Equal("Andes Logistica S.A.S.", Assert.Single(body.Items).Name);
    }

    private static async Task<CompaniesResponse> ListAsync(HttpClient client, string query)
    {
        var response = await client.GetAsync(
            $"{CompaniesUrl()}{query}", TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<CompaniesResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        return body;
    }
}
