using System.Net;
using System.Net.Http.Json;
using Modules.Customers.Application;
using static Modules.Customers.IntegrationTests.CustomersApiHarness;

namespace Modules.Customers.IntegrationTests;

public sealed class CustomerWriteApiTests
{
    [Fact]
    public async Task CreateReturnsCreatedWithTheLocationOfTheNewCustomer()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);

        var response = await client.PostAsJsonAsync(
            CustomersUrl(),
            new
            {
                name = "Verde Esencial S.A.S.",
                identificationType = "NIT",
                identificationNumber = "900.123.456-1",
                phone = "310 935 2187",
                email = "Compras@VerdeEsencial.CO",
                address = "Calle 10 # 45-12",
                department = "Antioquia",
                city = "Medellin",
                classification = "MEDIANO",
                withRetention = true
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var customer = await response.Content.ReadFromJsonAsync<CustomerResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(customer);
        Assert.True(customer.IsActive);
        Assert.True(customer.WithRetention);
        Assert.Equal("MEDIANO", customer.Classification);
        // Normalizado por el dominio: el correo baja a minusculas.
        Assert.Equal("compras@verdeesencial.co", customer.Email);
        Assert.Equal(
            $"{CustomersUrl()}/{customer.Id}",
            response.Headers.Location?.ToString());
    }

    /// <summary>
    /// El CUC lo emite el backend, no viaja en el request, y el consecutivo avanza por tenant.
    ///
    /// El formato es el que el consumidor ya espera y pinta en su propia columna:
    /// <c>CUC-000001</c> (<c>generateCuc</c> en <c>customers.fixtures.ts</c>).
    /// </summary>
    [Fact]
    public async Task CreateEmitsASequentialCucPerTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);

        var first = await CreateCustomerAsync(client, "Verde Esencial", "900.111.111-1");
        var second = await CreateCustomerAsync(client, "Naturaleza Viva", "830.222.222-2");

        Assert.Equal("CUC-000001", first.Cuc);
        Assert.Equal("CUC-000002", second.Cuc);
    }

    // Cada tenant tiene su propio consecutivo. Uno compartido le dejaria ver a un tenant cuantos
    // clientes cargo el otro con solo mirar el codigo que le toco — una fuga por un canal lateral,
    // sin una sola consulta cruzada.
    [Fact]
    public async Task TheCucSequenceIsIsolatedPerTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var mine = CreateManager(factory);
        using var theirs = CreateClient(
            factory,
            OtherSubjectId,
            OtherTenantId,
            CustomersPermissions.CustomerRead,
            CustomersPermissions.CustomerManage);

        await CreateCustomerAsync(mine, "Verde Esencial", "900.111.111-1");
        var theirFirst = await CreateCustomerAsync(
            theirs, "Otro Cliente", "830.222.222-2", OtherTenantId);

        Assert.Equal("CUC-000001", theirFirst.Cuc);
    }

    // La violacion de IX_customers_tenant_identification tiene que salir como 422 con su codigo, no
    // como un 500: es el unico arbitro real de la unicidad, y traducirla es de Infrastructure.
    [Fact]
    public async Task CreateRejectsADuplicateIdentification()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        await CreateCustomerAsync(client, "Verde Esencial", "900.123.456-1");

        var response = await client.PostAsJsonAsync(
            CustomersUrl(),
            NewCustomerBody(name: "Otro Cliente", identificationNumber: "900.123.456-1"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        Assert.Contains(
            "customers.customer.identification_taken", body, StringComparison.Ordinal);
    }

    // El numero se recorta antes de comparar: el indice unico trata " 900-1" y "900-1" como
    // distintos, cosa que nadie leyendo la lista haria.
    [Fact]
    public async Task CreateRejectsADuplicateThatOnlyDiffersInSurroundingWhitespace()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        await CreateCustomerAsync(client, "Verde Esencial", "900.123.456-1");

        var response = await client.PostAsJsonAsync(
            CustomersUrl(),
            NewCustomerBody(
                name: "Otro Cliente", identificationNumber: "  900.123.456-1  "),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    // El mismo numero con otro tipo de documento **no** es un duplicado: la clave es el par, y un
    // NIT 900-1 y una cedula 900-1 son dos personas distintas.
    [Fact]
    public async Task CreateAllowsTheSameNumberWithAnotherIdentificationType()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        await CreateCustomerAsync(client, "Verde Esencial", "900.123.456-1");

        var response = await client.PostAsJsonAsync(
            CustomersUrl(),
            new
            {
                name = "Persona Natural",
                identificationType = "CC",
                identificationNumber = "900.123.456-1",
                withRetention = false
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    /// <summary>
    /// El 422 de validacion tiene que traer el mapa <c>errors</c> con los nombres en PascalCase.
    /// Es el unico 422 que el formulario sabe leer: <c>customerFieldErrors</c> descarta el resto, y
    /// sin el mapa el input queda sin marcar. Es la trampa de <c>register-tenant</c>.
    /// </summary>
    [Fact]
    public async Task CreateWithInvalidFieldsReturnsThePerFieldErrorMap()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);

        var response = await client.PostAsJsonAsync(
            CustomersUrl(),
            new
            {
                name = "",
                identificationType = "DNI",
                identificationNumber = "",
                email = "no-es-un-correo",
                classification = "ENORME",
                withRetention = false
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var fields = await ValidationFieldsAsync(response);
        Assert.Contains("Name", fields);
        Assert.Contains("IdentificationType", fields);
        Assert.Contains("IdentificationNumber", fields);
        Assert.Contains("Email", fields);
        Assert.Contains("Classification", fields);
    }

    // Vacio es ausente para un campo opcional: el formulario manda "" cuando el usuario borra el
    // input, y rechazarlo bloquearia el alta de un cliente que legitimamente no tiene correo.
    [Fact]
    public async Task CreateAcceptsBlankOptionalFieldsAndStoresThemAsNull()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);

        var response = await client.PostAsJsonAsync(
            CustomersUrl(),
            new
            {
                name = "Verde Esencial S.A.S.",
                identificationType = "NIT",
                identificationNumber = "900.123.456-1",
                phone = "",
                email = "",
                address = "",
                department = "",
                city = "",
                classification = "",
                withRetention = false
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var customer = await response.Content.ReadFromJsonAsync<CustomerResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(customer);
        Assert.Null(customer.Phone);
        Assert.Null(customer.Email);
        Assert.Null(customer.Address);
        Assert.Null(customer.Department);
        Assert.Null(customer.City);
        Assert.Null(customer.Classification);
    }

    [Fact]
    public async Task CreateWithoutTheManagePermissionIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(
            factory, SubjectId, TenantId, CustomersPermissions.CustomerRead);

        var response = await client.PostAsJsonAsync(
            CustomersUrl(),
            NewCustomerBody(),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// El handler autoriza **antes** de validar. Este llamador tiene el permiso pero para otro
    /// tenant, y manda un cuerpo invalido: si el orden estuviera al reves se llevaria el mapa de
    /// errores por campo —la forma del contrato— antes de que nadie le diga que no. Lo encontro la
    /// revision de riesgo de CAT-02.
    /// </summary>
    [Fact]
    public async Task CreateForAnotherTenantIsForbiddenBeforeTheBodyIsValidated()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(
            factory, OtherSubjectId, OtherTenantId, CustomersPermissions.CustomerManage);

        var response = await client.PostAsJsonAsync(
            CustomersUrl(),
            new
            {
                name = "",
                identificationType = "",
                identificationNumber = "",
                withRetention = false
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        Assert.DoesNotContain("errors", body, StringComparison.Ordinal);
    }

    // El PUT reemplaza el recurso entero: un campo ausente se **limpia**. Una implementacion que
    // ignore los null "para no pisar" deja campos imborrables y pasa todas las demas pruebas.
    [Fact]
    public async Task UpdateClearsTheOptionalFieldsThatArriveNull()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var created = await client.PostAsJsonAsync(
            CustomersUrl(),
            new
            {
                name = "Verde Esencial S.A.S.",
                identificationType = "NIT",
                identificationNumber = "900.123.456-1",
                phone = "310 935 2187",
                email = "compras@verde.co",
                address = "Calle 10 # 45-12",
                department = "Antioquia",
                city = "Medellin",
                classification = "GRANDE",
                withRetention = true
            },
            TestContext.Current.CancellationToken);
        created.EnsureSuccessStatusCode();
        var customer = await created.Content.ReadFromJsonAsync<CustomerResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(customer);

        var response = await client.PutAsJsonAsync(
            $"{CustomersUrl()}/{customer.Id}",
            NewCustomerBody(),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<CustomerResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(updated);
        Assert.Null(updated.Phone);
        Assert.Null(updated.Email);
        Assert.Null(updated.Address);
        Assert.Null(updated.Department);
        Assert.Null(updated.City);
        Assert.Null(updated.Classification);
        Assert.False(updated.WithRetention);
    }

    // El CUC no viaja en el request y el PUT no lo puede tocar. Es el identificador con el que una
    // persona habla del cliente por telefono; volverlo mutable hace que la conversacion de ayer
    // deje de referirse a nadie.
    [Fact]
    public async Task UpdateNeverChangesTheCuc()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var created = await CreateCustomerAsync(client);

        var response = await client.PutAsJsonAsync(
            $"{CustomersUrl()}/{created.Id}",
            new
            {
                name = "Verde Esencial S.A.",
                identificationType = "NIT",
                identificationNumber = "900.123.456-1",
                cuc = "CUC-999999",
                withRetention = false
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<CustomerResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(updated);
        Assert.Equal(created.Cuc, updated.Cuc);
    }

    [Fact]
    public async Task UpdateAnUnknownCustomerIsNotFound()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);

        var response = await client.PutAsJsonAsync(
            $"{CustomersUrl()}/{Guid.CreateVersion7()}",
            NewCustomerBody(),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // La otra cara de la unicidad: guardar sin tocar el documento no puede chocar consigo mismo.
    // Una comprobacion escrita con un SELECT ingenuo falla justo aca.
    [Fact]
    public async Task UpdateKeepingItsOwnIdentificationIsAllowed()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var created = await CreateCustomerAsync(client);

        var response = await client.PutAsJsonAsync(
            $"{CustomersUrl()}/{created.Id}",
            NewCustomerBody(name: "Verde Esencial S.A."),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var customer = await response.Content.ReadFromJsonAsync<CustomerResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(customer);
        Assert.Equal("Verde Esencial S.A.", customer.Name);
    }

    // El id de otro tenant no se alcanza ni con el permiso puesto: la autorizacion corta antes de
    // consultar el repositorio, asi que responde 403 y no 404 — un 404 confirmaria que existe.
    [Fact]
    public async Task UpdateACustomerOfAnotherTenantIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var mine = CreateManager(factory);
        using var theirs = CreateClient(
            factory, OtherSubjectId, OtherTenantId, CustomersPermissions.CustomerManage);
        var created = await CreateCustomerAsync(mine);

        var response = await theirs.PutAsJsonAsync(
            $"{CustomersUrl()}/{created.Id}",
            NewCustomerBody(name: "Robado"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
