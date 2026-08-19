using System.Net;
using System.Net.Http.Json;
using Modules.Companies.Application;
using static Modules.Companies.IntegrationTests.CompaniesApiHarness;

namespace Modules.Companies.IntegrationTests;

public sealed class CompanyWriteApiTests
{
    [Fact]
    public async Task CreateReturnsCreatedWithTheLocationOfTheNewCompany()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);

        var response = await client.PostAsJsonAsync(
            CompaniesUrl(),
            new
            {
                name = "Andes Logistica S.A.S.",
                accountNumber = "CTA-000123",
                taxId = "900.111.222-3",
                phone = "310 555 1122",
                email = "Contacto@Andes.CO",
                address = "Calle 80 # 45-12, Bogota"
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var company = await response.Content.ReadFromJsonAsync<CompanyResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(company);
        Assert.True(company.IsActive);
        // Normalizado por el dominio: el correo baja a minusculas, y la ruta del Location es la
        // tenant-scoped, no la especulativa que el consumidor llamaba antes.
        Assert.Equal("contacto@andes.co", company.Email);
        Assert.Equal(
            $"{CompaniesUrl()}/{company.Id}",
            response.Headers.Location?.ToString());
    }

    // La violacion de IX_companies_tenant_account_number tiene que salir como 422 con su codigo,
    // no como un 500: es el unico arbitro real de la unicidad, y traducirla es de Infrastructure.
    [Fact]
    public async Task CreateRejectsADuplicateAccountNumber()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        await CreateCompanyAsync(client, "Andes Logistica S.A.S.", "CTA-000123");

        var response = await client.PostAsJsonAsync(
            CompaniesUrl(),
            new
            {
                name = "Otra Empresa S.A.S.",
                accountNumber = "CTA-000123",
                taxId = "830.222.333-4"
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        Assert.Contains(
            "companies.company.account_number_taken", body, StringComparison.Ordinal);
    }

    // El numero de cuenta se recorta antes de comparar: el indice unico trata " CTA-1" y "CTA-1"
    // como distintos, cosa que nadie leyendo la lista haria.
    [Fact]
    public async Task CreateRejectsADuplicateThatOnlyDiffersInSurroundingWhitespace()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        await CreateCompanyAsync(client, "Andes Logistica S.A.S.", "CTA-000123");

        var response = await client.PostAsJsonAsync(
            CompaniesUrl(),
            new
            {
                name = "Otra Empresa S.A.S.",
                accountNumber = "  CTA-000123  ",
                taxId = "830.222.333-4"
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task CreateWithoutTheManagePermissionIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(
            factory, SubjectId, TenantId, CompaniesPermissions.CompanyRead);

        var response = await client.PostAsJsonAsync(
            CompaniesUrl(),
            new { name = "Andes", accountNumber = "CTA-1", taxId = "900-1" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// El handler autoriza **antes** de validar. Este llamador tiene el permiso pero para otro
    /// tenant, y manda un cuerpo invalido: si el orden estuviera al reves se llevaria el mapa de
    /// errores por campo —la forma del contrato— antes de que nadie le diga que no. Lo encontro
    /// la revision de riesgo de CAT-02.
    /// </summary>
    [Fact]
    public async Task CreateForAnotherTenantIsForbiddenBeforeTheBodyIsValidated()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(
            factory, OtherSubjectId, OtherTenantId, CompaniesPermissions.CompanyManage);

        var response = await client.PostAsJsonAsync(
            CompaniesUrl(),
            new { name = "", accountNumber = "", taxId = "" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        Assert.DoesNotContain("errors", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// El 422 de validacion tiene que traer el mapa <c>errors</c> con los nombres en PascalCase.
    /// Es el unico 422 que el formulario sabe leer: <c>companyFieldErrors</c> descarta el resto,
    /// y sin el mapa el input queda sin marcar. Es la trampa de <c>register-tenant</c>.
    /// </summary>
    [Fact]
    public async Task CreateWithInvalidFieldsReturnsThePerFieldErrorMap()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);

        var response = await client.PostAsJsonAsync(
            CompaniesUrl(),
            new
            {
                name = "",
                accountNumber = new string('1', 33),
                taxId = "",
                email = "no-es-un-correo"
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var fields = await ValidationFieldsAsync(response);
        Assert.Contains("Name", fields);
        Assert.Contains("AccountNumber", fields);
        Assert.Contains("TaxId", fields);
        Assert.Contains("Email", fields);
    }

    // Vacio es ausente para un campo opcional: el formulario manda "" cuando el usuario borra el
    // input, y rechazarlo bloquearia el alta de una empresa que legitimamente no tiene correo.
    [Fact]
    public async Task CreateAcceptsBlankOptionalFieldsAndStoresThemAsNull()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);

        var response = await client.PostAsJsonAsync(
            CompaniesUrl(),
            new
            {
                name = "Andes Logistica S.A.S.",
                accountNumber = "CTA-000123",
                taxId = "900.111.222-3",
                phone = "",
                email = "",
                address = ""
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var company = await response.Content.ReadFromJsonAsync<CompanyResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(company);
        Assert.Null(company.Phone);
        Assert.Null(company.Email);
        Assert.Null(company.Address);
    }

    // El PUT reemplaza el recurso entero: un campo ausente se **limpia**. Una implementacion que
    // ignore los null "para no pisar" deja campos imborrables y pasa todas las demas pruebas.
    [Fact]
    public async Task UpdateClearsTheOptionalFieldsThatArriveNull()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var created = await CreateCompanyAsync(
            client,
            "Andes Logistica S.A.S.",
            "CTA-000123",
            phone: "310 555 1122",
            email: "contacto@andes.co",
            address: "Calle 80 # 45-12");

        var response = await client.PutAsJsonAsync(
            $"{CompaniesUrl()}/{created.Id}",
            new
            {
                name = "Andes Logistica S.A.S.",
                accountNumber = "CTA-000123",
                taxId = "900.111.222-3"
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var company = await response.Content.ReadFromJsonAsync<CompanyResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(company);
        Assert.Null(company.Phone);
        Assert.Null(company.Email);
        Assert.Null(company.Address);
    }

    [Fact]
    public async Task UpdateAnUnknownCompanyIsNotFound()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);

        var response = await client.PutAsJsonAsync(
            $"{CompaniesUrl()}/{Guid.CreateVersion7()}",
            new { name = "Andes", accountNumber = "CTA-1", taxId = "900-1" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UpdateToAnAccountNumberTakenByAnotherCompanyIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        await CreateCompanyAsync(client, "Andes Logistica S.A.S.", "CTA-000123");
        var second = await CreateCompanyAsync(client, "Textiles Andinos", "CTA-000456");

        var response = await client.PutAsJsonAsync(
            $"{CompaniesUrl()}/{second.Id}",
            new
            {
                name = "Textiles Andinos",
                accountNumber = "CTA-000123",
                taxId = "900.444.555-6"
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        Assert.Contains(
            "companies.company.account_number_taken", body, StringComparison.Ordinal);
    }

    // La otra cara de la anterior: guardar sin tocar el numero de cuenta no puede chocar consigo
    // misma. Una comprobacion de unicidad escrita con un SELECT ingenuo falla justo aca.
    [Fact]
    public async Task UpdateKeepingItsOwnAccountNumberIsAllowed()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var created = await CreateCompanyAsync(client, "Andes Logistica S.A.S.", "CTA-000123");

        var response = await client.PutAsJsonAsync(
            $"{CompaniesUrl()}/{created.Id}",
            new
            {
                name = "Andes Logistica S.A.",
                accountNumber = "CTA-000123",
                taxId = "900.111.222-3"
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var company = await response.Content.ReadFromJsonAsync<CompanyResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(company);
        Assert.Equal("Andes Logistica S.A.", company.Name);
    }

    // El id de otro tenant no se alcanza ni con el permiso puesto: la autorizacion corta antes de
    // consultar el repositorio, asi que responde 403 y no 404 — un 404 confirmaria que existe.
    [Fact]
    public async Task UpdateACompanyOfAnotherTenantIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var mine = CreateManager(factory);
        using var theirs = CreateClient(
            factory, OtherSubjectId, OtherTenantId, CompaniesPermissions.CompanyManage);
        var created = await CreateCompanyAsync(mine, "Andes Logistica S.A.S.", "CTA-000123");

        var response = await theirs.PutAsJsonAsync(
            $"{CompaniesUrl()}/{created.Id}",
            new { name = "Robada", accountNumber = "CTA-000123", taxId = "900-1" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
