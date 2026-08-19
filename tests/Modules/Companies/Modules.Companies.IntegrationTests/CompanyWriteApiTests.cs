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
                bankAccounts = new[] { BankAccount("CTA-000123") },
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

    // La coleccion viaja completa de vuelta, en el orden en que se mando: es lo que el formulario
    // vuelve a pintar al editar, y un orden que no se conserva se lee como si el usuario hubiera
    // cargado otra cosa.
    [Fact]
    public async Task CreateStoresEveryAccountInOrder()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);

        var response = await client.PostAsJsonAsync(
            CompaniesUrl(),
            new
            {
                name = "Andes Logistica S.A.S.",
                bankAccounts = new[]
                {
                    BankAccount("CTA-000123"),
                    BankAccount("CTA-000456", bankName: "Davivienda", currency: "usd")
                },
                taxId = "900.111.222-3"
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var company = await response.Content.ReadFromJsonAsync<CompanyResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(company);
        Assert.Collection(
            company.BankAccounts,
            first =>
            {
                Assert.Equal("Bancolombia", first.BankName);
                Assert.Equal("CTA-000123", first.AccountNumber);
                Assert.Equal("COP", first.Currency);
            },
            second =>
            {
                Assert.Equal("Davivienda", second.BankName);
                Assert.Equal("CTA-000456", second.AccountNumber);
                // Normalizada a mayusculas por el dominio, como la moneda de catalogo.
                Assert.Equal("USD", second.Currency);
            });
    }

    /// <summary>
    /// Lo que reemplazo a <c>IX_companies_tenant_account_number</c>: el duplicado que se rechaza es
    /// el de **la misma empresa**, no el del tenant.
    ///
    /// Y sale con el mapa <c>errors</c>, no como codigo de dominio suelto. Si solo lo atajara el
    /// dominio, el formulario mostraria "revisa los datos marcados" sin marcar ninguna fila — que
    /// para una lista repetible es lo mismo que no decir nada.
    /// </summary>
    [Fact]
    public async Task CreateRejectsTheSameAccountTwiceInOneCompany()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);

        var response = await client.PostAsJsonAsync(
            CompaniesUrl(),
            new
            {
                name = "Andes Logistica S.A.S.",
                bankAccounts = new[]
                {
                    BankAccount("CTA-000123"),
                    // Solo difiere en espacios y en la caja del banco. Comparar sin normalizar
                    // dejaria pasar las dos.
                    BankAccount("  CTA-000123  ", bankName: " bancolombia ")
                },
                taxId = "900.111.222-3"
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("BankAccounts", await ValidationFieldsAsync(response));
    }

    // La contracara, y el cambio de contrato de EMP-08: dos empresas del mismo tenant **si** pueden
    // compartir numero de cuenta. Antes esto era un 422; el indice unico que lo prohibia no salia
    // de RF-091, que solo pide registrar el numero.
    [Fact]
    public async Task CreateAllowsAnAccountNumberAnotherCompanyAlreadyUses()
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
                bankAccounts = new[] { BankAccount("CTA-000123") },
                taxId = "830.222.333-4"
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    // El mismo numero en otro banco no es un duplicado: son dos cuentas reales. Que la clave sea la
    // terna y no el numero solo es lo que hace util a la pantalla.
    [Fact]
    public async Task CreateAllowsTheSameNumberInAnotherBank()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);

        var response = await client.PostAsJsonAsync(
            CompaniesUrl(),
            new
            {
                name = "Andes Logistica S.A.S.",
                bankAccounts = new[]
                {
                    BankAccount("CTA-000123", bankName: "Bancolombia"),
                    BankAccount("CTA-000123", bankName: "Davivienda")
                },
                taxId = "900.111.222-3"
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var company = await response.Content.ReadFromJsonAsync<CompanyResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(company);
        Assert.Equal(2, company.BankAccounts.Count);
    }

    // Una empresa sin ninguna cuenta no es un estado valido: AccountNumber era NOT NULL antes de
    // EMP-08 y el modulo no baja esa garantia de paso. La lista vacia es lo que produce quitar la
    // ultima fila del formulario, asi que el 422 tiene que ser explicito y con su campo marcado.
    [Fact]
    public async Task CreateWithoutAnyAccountIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);

        var response = await client.PostAsJsonAsync(
            CompaniesUrl(),
            new
            {
                name = "Andes Logistica S.A.S.",
                bankAccounts = Array.Empty<object>(),
                taxId = "900.111.222-3"
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("BankAccounts", await ValidationFieldsAsync(response));
    }

    // La lista ausente del JSON llega como null y no como lista vacia. Sin la regla NotEmpty
    // llegaria al dominio como null y saldria como 500 en vez de 422.
    [Fact]
    public async Task CreateWithTheAccountListMissingIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);

        var response = await client.PostAsJsonAsync(
            CompaniesUrl(),
            new { name = "Andes Logistica S.A.S.", taxId = "900.111.222-3" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("BankAccounts", await ValidationFieldsAsync(response));
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
            new
            {
                name = "Andes",
                bankAccounts = new[] { BankAccount("CTA-1") },
                taxId = "900-1"
            },
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
            new { name = "", bankAccounts = Array.Empty<object>(), taxId = "" },
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
                bankAccounts = new[] { BankAccount("CTA-1") },
                taxId = "",
                email = "no-es-un-correo"
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var fields = await ValidationFieldsAsync(response);
        Assert.Contains("Name", fields);
        Assert.Contains("TaxId", fields);
        Assert.Contains("Email", fields);
    }

    /// <summary>
    /// El error de una fila llega **indexado**: <c>BankAccounts[1].Currency</c>, no
    /// <c>Currency</c>.
    ///
    /// Es lo que distingue marcar la fila correcta de marcar cualquiera. Con un nombre plano el
    /// formulario no tiene forma de saber cual de las cinco cuentas tiene la moneda mal, y pintar
    /// el error en la primera es peor que no pintarlo: manda a corregir un campo que esta bien.
    /// </summary>
    [Fact]
    public async Task CreateReportsTheFailingAccountByItsIndex()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);

        var response = await client.PostAsJsonAsync(
            CompaniesUrl(),
            new
            {
                name = "Andes Logistica S.A.S.",
                bankAccounts = new[]
                {
                    BankAccount("CTA-000123"),
                    BankAccount("CTA-000456", currency: "PESOS"),
                    BankAccount("CTA-000789", bankName: "")
                },
                taxId = "900.111.222-3"
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var fields = await ValidationFieldsAsync(response);
        Assert.Contains("BankAccounts[1].Currency", fields);
        Assert.Contains("BankAccounts[2].BankName", fields);
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
                bankAccounts = new[] { BankAccount("CTA-000123") },
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
                bankAccounts = new[] { BankAccount("CTA-000123") },
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

    /// <summary>
    /// Quitar una cuenta es mandar el PUT sin ella. La coleccion se reemplaza entera, igual que los
    /// tres opcionales, y esta prueba es la que impide que alguien la implemente como "agregar lo
    /// que llega" — que pasaria todas las demas y dejaria cuentas imborrables.
    /// </summary>
    [Fact]
    public async Task UpdateReplacesTheWholeAccountCollection()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);

        var created = await client.PostAsJsonAsync(
            CompaniesUrl(),
            new
            {
                name = "Andes Logistica S.A.S.",
                bankAccounts = new[]
                {
                    BankAccount("CTA-000123"),
                    BankAccount("CTA-000456", bankName: "Davivienda"),
                    BankAccount("CTA-000789", bankName: "BBVA")
                },
                taxId = "900.111.222-3"
            },
            TestContext.Current.CancellationToken);
        created.EnsureSuccessStatusCode();
        var company = await created.Content.ReadFromJsonAsync<CompanyResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(company);

        var response = await client.PutAsJsonAsync(
            $"{CompaniesUrl()}/{company.Id}",
            new
            {
                name = "Andes Logistica S.A.S.",
                bankAccounts = new[] { BankAccount("CTA-000456", bankName: "Davivienda") },
                taxId = "900.111.222-3"
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<CompanyResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(updated);
        var account = Assert.Single(updated.BankAccounts);
        Assert.Equal("CTA-000456", account.AccountNumber);

        // Y que persista, no solo que lo diga la respuesta del PUT: un GET posterior lee de la base
        // y no de la instancia que quedo en memoria.
        var reread = await client.GetFromJsonAsync<CompanyResponse>(
            $"{CompaniesUrl()}/{company.Id}",
            TestContext.Current.CancellationToken);
        Assert.NotNull(reread);
        Assert.Equal("CTA-000456", Assert.Single(reread.BankAccounts).AccountNumber);
    }

    [Fact]
    public async Task UpdateWithoutAnyAccountIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var created = await CreateCompanyAsync(client, "Andes Logistica S.A.S.", "CTA-000123");

        var response = await client.PutAsJsonAsync(
            $"{CompaniesUrl()}/{created.Id}",
            new
            {
                name = "Andes Logistica S.A.S.",
                bankAccounts = Array.Empty<object>(),
                taxId = "900.111.222-3"
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        // El rechazo no puede haber dejado la empresa sin cuentas. Update muta en el sitio, asi que
        // vaciar la coleccion antes de validar dejaria este GET devolviendo una lista vacia aunque
        // el 422 diga que no se guardo nada.
        var reread = await client.GetFromJsonAsync<CompanyResponse>(
            $"{CompaniesUrl()}/{created.Id}",
            TestContext.Current.CancellationToken);
        Assert.NotNull(reread);
        Assert.Equal("CTA-000123", Assert.Single(reread.BankAccounts).AccountNumber);
    }

    [Fact]
    public async Task UpdateAnUnknownCompanyIsNotFound()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);

        var response = await client.PutAsJsonAsync(
            $"{CompaniesUrl()}/{Guid.CreateVersion7()}",
            new
            {
                name = "Andes",
                bankAccounts = new[] { BankAccount("CTA-1") },
                taxId = "900-1"
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // Guardar sin tocar las cuentas no puede chocar consigo misma. Una comprobacion de unicidad
    // escrita con un SELECT ingenuo falla justo aca.
    [Fact]
    public async Task UpdateKeepingItsOwnAccountsIsAllowed()
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
                bankAccounts = new[] { BankAccount("CTA-000123") },
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
            new
            {
                name = "Robada",
                bankAccounts = new[] { BankAccount("CTA-000123") },
                taxId = "900-1"
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
