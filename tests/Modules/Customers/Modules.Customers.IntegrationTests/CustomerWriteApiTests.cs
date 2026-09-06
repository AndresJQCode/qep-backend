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
        var city = await EnsureCityAsync(client);
        var classification = await CreateClassificationAsync(client);

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
                cityId = city.CityId,
                classificationId = classification.Id,
                withRetention = true
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var customer = await response.Content.ReadFromJsonAsync<CustomerResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(customer);
        Assert.True(customer.IsActive);
        Assert.True(customer.WithRetention);
        Assert.Equal(classification.Id, customer.Classification.Id);
        Assert.Equal(city.CityId, customer.City.Id);
        Assert.Equal(city.DepartmentDivipolaCode, customer.Department.DivipolaCode);
        // Normalizado por el dominio: el correo baja a minusculas.
        Assert.Equal("compras@verdeesencial.co", customer.Email);
        Assert.Equal(
            $"{CustomersUrl()}/{customer.Id}",
            response.Headers.Location?.ToString());
    }

    /// <summary>
    /// El CUC lo emite el backend, no viaja en el request, y el consecutivo avanza por tenant.
    ///
    /// El formato es <c>{prefijo}{depto}{consecutivo}</c> (Fase 4): el prefijo de la clasificacion
    /// del cliente, el codigo DIVIPOLA del departamento de su ciudad y un consecutivo de seis
    /// digitos. Ciudad y clasificacion se mantienen fijas entre las dos altas para que el
    /// prefijo/departamento no varien y la asercion se concentre en el consecutivo.
    /// </summary>
    [Fact]
    public async Task CreateEmitsASequentialCucPerTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var classification = await CreateClassificationAsync(client, "Mediano", "CLI");

        var first = await CreateCustomerAsync(
            client, city.CityId, classification.Id, "Verde Esencial", "900.111.111-1");
        var second = await CreateCustomerAsync(
            client, city.CityId, classification.Id, "Naturaleza Viva", "830.222.222-2");

        var expectedPrefix = $"CLI{city.DepartmentDivipolaCode}";
        Assert.Equal($"{expectedPrefix}000001", first.Cuc);
        Assert.Equal($"{expectedPrefix}000002", second.Cuc);
    }

    // El consecutivo es un unico contador por tenant, no uno por clasificacion: dos clientes del
    // mismo tenant con clasificaciones (y por lo tanto prefijos) distintas siguen compartiendo el
    // mismo numero de secuencia, aunque el CUC final se vea distinto por el prefijo.
    [Fact]
    public async Task TheCucSequenceIsSharedAcrossDifferentClassificationsOfTheSameTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var retail = await CreateClassificationAsync(client, "Minorista", "MIN");
        var wholesale = await CreateClassificationAsync(client, "Mayorista", "MAY");

        var first = await CreateCustomerAsync(
            client, city.CityId, retail.Id, "Verde Esencial", "900.111.111-1");
        var second = await CreateCustomerAsync(
            client, city.CityId, wholesale.Id, "Naturaleza Viva", "830.222.222-2");

        Assert.Equal($"MIN{city.DepartmentDivipolaCode}000001", first.Cuc);
        Assert.Equal($"MAY{city.DepartmentDivipolaCode}000002", second.Cuc);
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
            CustomersPermissions.CustomerManage,
            CustomersPermissions.ClassificationRead,
            CustomersPermissions.ClassificationManage);
        var myCity = await EnsureCityAsync(mine);
        var myClassification = await CreateClassificationAsync(mine, "Mediano", "CLI");
        var theirCity = await EnsureCityAsync(theirs);
        var theirClassification = await CreateClassificationAsync(
            theirs, "Mediano", "CLI", OtherTenantId);

        await CreateCustomerAsync(
            mine, myCity.CityId, myClassification.Id, "Verde Esencial", "900.111.111-1");
        var theirFirst = await CreateCustomerAsync(
            theirs,
            theirCity.CityId,
            theirClassification.Id,
            "Otro Cliente",
            "830.222.222-2",
            OtherTenantId);

        Assert.Equal($"CLI{theirCity.DepartmentDivipolaCode}000001", theirFirst.Cuc);
    }

    // Referenciar una ciudad o una clasificacion que no existe (o que existe pero en otro tenant)
    // sale como 422 con su propio codigo de dominio, no como un 500: es exactamente el mismo
    // criterio que Catalog ya usa para Product.TaxRateId.
    [Fact]
    public async Task CreateWithAnUnknownCityIsRejectedWithItsDomainCode()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var classification = await CreateClassificationAsync(client);

        var response = await client.PostAsJsonAsync(
            CustomersUrl(),
            NewCustomerBody(Guid.CreateVersion7(), classification.Id),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("customers.customer.city_not_found", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateWithAnUnknownClassificationIsRejectedWithItsDomainCode()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);

        var response = await client.PostAsJsonAsync(
            CustomersUrl(),
            NewCustomerBody(city.CityId, Guid.CreateVersion7()),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains(
            "customers.customer.classification_not_found", body, StringComparison.Ordinal);
    }

    // Una clasificacion de otro tenant no es "no encontrada por casualidad": el repositorio filtra
    // por tenant, asi que referenciarla desde este tenant tiene que fallar igual que si no
    // existiera en absoluto — nunca prestada entre tenants.
    [Fact]
    public async Task CreateWithAnotherTenantsClassificationIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var mine = CreateManager(factory);
        using var theirs = CreateClient(
            factory,
            OtherSubjectId,
            OtherTenantId,
            CustomersPermissions.ClassificationRead,
            CustomersPermissions.ClassificationManage);
        var city = await EnsureCityAsync(mine);
        var theirClassification = await CreateClassificationAsync(
            theirs, "Ajena", "AJE", OtherTenantId);

        var response = await mine.PostAsJsonAsync(
            CustomersUrl(),
            NewCustomerBody(city.CityId, theirClassification.Id),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    // La violacion de IX_customers_tenant_identification tiene que salir como 422 con su codigo, no
    // como un 500: es el unico arbitro real de la unicidad, y traducirla es de Infrastructure.
    [Fact]
    public async Task CreateRejectsADuplicateIdentification()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var classification = await CreateClassificationAsync(client);
        await CreateCustomerAsync(
            client, city.CityId, classification.Id, "Verde Esencial", "900.123.456-1");

        var response = await client.PostAsJsonAsync(
            CustomersUrl(),
            NewCustomerBody(
                city.CityId,
                classification.Id,
                name: "Otro Cliente",
                identificationNumber: "900.123.456-1"),
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
        var city = await EnsureCityAsync(client);
        var classification = await CreateClassificationAsync(client);
        await CreateCustomerAsync(
            client, city.CityId, classification.Id, "Verde Esencial", "900.123.456-1");

        var response = await client.PostAsJsonAsync(
            CustomersUrl(),
            NewCustomerBody(
                city.CityId,
                classification.Id,
                name: "Otro Cliente",
                identificationNumber: "  900.123.456-1  "),
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
        var city = await EnsureCityAsync(client);
        var classification = await CreateClassificationAsync(client);
        await CreateCustomerAsync(
            client, city.CityId, classification.Id, "Verde Esencial", "900.123.456-1");

        var response = await client.PostAsJsonAsync(
            CustomersUrl(),
            new
            {
                name = "Persona Natural",
                identificationType = "CC",
                identificationNumber = "900.123.456-1",
                address = "Calle 10 # 45-12",
                cityId = city.CityId,
                classificationId = classification.Id,
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
                cityId = Guid.Empty,
                classificationId = Guid.Empty,
                withRetention = false
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var fields = await ValidationFieldsAsync(response);
        Assert.Contains("Name", fields);
        Assert.Contains("IdentificationType", fields);
        Assert.Contains("IdentificationNumber", fields);
        Assert.Contains("Email", fields);
        Assert.Contains("CityId", fields);
        Assert.Contains("ClassificationId", fields);
    }

    // La direccion dejo de ser un campo opcional del cliente cuando nacio la libreta (028afe2):
    // el alta crea la principal --su ciudad emite el CUC y la cotizacion la propone por
    // defecto-- y una direccion sin calle no es una direccion. El rechazo tiene que llegar como
    // validation.failed con el mapa errors, no como el 422 pelado del dominio: es el unico que
    // el formulario sabe leer para marcar el input.
    [Fact]
    public async Task CreateWithoutAnAddressMarksTheAddressField()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var classification = await CreateClassificationAsync(client);

        var response = await client.PostAsJsonAsync(
            CustomersUrl(),
            new
            {
                name = "Verde Esencial S.A.S.",
                identificationType = "NIT",
                identificationNumber = "900.123.456-1",
                address = "",
                cityId = city.CityId,
                classificationId = classification.Id,
                withRetention = false
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("Address", await ValidationFieldsAsync(response));
    }

    // Vacio es ausente para un campo opcional: el formulario manda "" cuando el usuario borra el
    // input, y rechazarlo bloquearia el alta de un cliente que legitimamente no tiene correo.
    // `address` ya no esta en esta bolsa: la libreta (028afe2) la volvio obligatoria, y su caso
    // lo cubre CreateWithoutAnAddressMarksTheAddressField.
    [Fact]
    public async Task CreateAcceptsBlankOptionalFieldsAndStoresThemAsNull()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var classification = await CreateClassificationAsync(client);

        var response = await client.PostAsJsonAsync(
            CustomersUrl(),
            new
            {
                name = "Verde Esencial S.A.S.",
                identificationType = "NIT",
                identificationNumber = "900.123.456-1",
                phone = "",
                email = "",
                address = "Calle 10 # 45-12",
                cityId = city.CityId,
                classificationId = classification.Id,
                withRetention = false
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var customer = await response.Content.ReadFromJsonAsync<CustomerResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(customer);
        Assert.Null(customer.Phone);
        Assert.Null(customer.Email);
        Assert.Equal("Calle 10 # 45-12", customer.Address);
    }

    [Fact]
    public async Task CreateWithoutTheManagePermissionIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var manager = CreateManager(factory);
        var city = await EnsureCityAsync(manager);
        var classification = await CreateClassificationAsync(manager);
        using var client = CreateClient(
            factory, SubjectId, TenantId, CustomersPermissions.CustomerRead);

        var response = await client.PostAsJsonAsync(
            CustomersUrl(),
            NewCustomerBody(city.CityId, classification.Id),
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
                cityId = Guid.Empty,
                classificationId = Guid.Empty,
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
        var city = await EnsureCityAsync(client);
        var classification = await CreateClassificationAsync(client);
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
                cityId = city.CityId,
                classificationId = classification.Id,
                withRetention = true
            },
            TestContext.Current.CancellationToken);
        created.EnsureSuccessStatusCode();
        var customer = await created.Content.ReadFromJsonAsync<CustomerResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(customer);

        var response = await client.PutAsJsonAsync(
            $"{CustomersUrl()}/{customer.Id}",
            NewCustomerBody(city.CityId, classification.Id),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<CustomerResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(updated);
        Assert.Null(updated.Phone);
        Assert.Null(updated.Email);
        // `address` ya no se limpia: la libreta (028afe2) la volvio obligatoria, asi que el PUT
        // la reemplaza por la que trae el cuerpo en vez de dejarla en null.
        Assert.Equal("Calle 10 # 45-12", updated.Address);
        Assert.False(updated.WithRetention);
    }

    // Mismo criterio que withRetention: viaja en el POST/PUT, se guarda y se devuelve tal cual.
    [Fact]
    public async Task CreateAndUpdateRoundTripVatSurplus()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var classification = await CreateClassificationAsync(client);

        var created = await client.PostAsJsonAsync(
            CustomersUrl(),
            NewCustomerBody(city.CityId, classification.Id, vatSurplus: true),
            TestContext.Current.CancellationToken);
        created.EnsureSuccessStatusCode();
        var customer = await created.Content.ReadFromJsonAsync<CustomerResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(customer);
        Assert.True(customer.VatSurplus);

        var updated = await client.PutAsJsonAsync(
            $"{CustomersUrl()}/{customer.Id}",
            NewCustomerBody(city.CityId, classification.Id, vatSurplus: false),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var result = await updated.Content.ReadFromJsonAsync<CustomerResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.False(result.VatSurplus);
    }

    // El CUC no viaja en el request y el PUT no lo puede pisar con un valor propio ("cuc":
    // "CUC-999999" aca no tiene efecto). Sin cambio de clasificacion, tampoco cambia por si solo.
    [Fact]
    public async Task UpdateKeepsTheCucWhenTheClassificationDoesNotChange()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var classification = await CreateClassificationAsync(client);
        var created = await CreateCustomerAsync(client, city.CityId, classification.Id);

        var response = await client.PutAsJsonAsync(
            $"{CustomersUrl()}/{created.Id}",
            new
            {
                name = "Verde Esencial S.A.",
                identificationType = "NIT",
                identificationNumber = "900.123.456-1",
                cuc = "CUC-999999",
                address = "Calle 10 # 45-12",
                cityId = city.CityId,
                classificationId = classification.Id,
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
        var city = await EnsureCityAsync(client);
        var classification = await CreateClassificationAsync(client);

        var response = await client.PutAsJsonAsync(
            $"{CustomersUrl()}/{Guid.CreateVersion7()}",
            NewCustomerBody(city.CityId, classification.Id),
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
        var city = await EnsureCityAsync(client);
        var classification = await CreateClassificationAsync(client);
        var created = await CreateCustomerAsync(client, city.CityId, classification.Id);

        var response = await client.PutAsJsonAsync(
            $"{CustomersUrl()}/{created.Id}",
            NewCustomerBody(city.CityId, classification.Id, name: "Verde Esencial S.A."),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var customer = await response.Content.ReadFromJsonAsync<CustomerResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(customer);
        Assert.Equal("Verde Esencial S.A.", customer.Name);
    }

    // La ciudad y la clasificacion se pueden reemplazar en un PUT: un cliente se puede mudar de
    // ciudad o cambiar de categoria. Regla de negocio confirmada: cambiar la clasificacion (el
    // "tamano" del cliente) reescribe unicamente el prefijo del CUC — el departamento y el
    // consecutivo, sus ultimos ocho caracteres, se conservan intactos.
    [Fact]
    public async Task UpdateCanChangeTheCityAndTheClassification()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var classification = await CreateClassificationAsync(client, "Mediano", "CLI");
        var newClassification = await CreateClassificationAsync(client, "Grande", "GRA");
        var created = await CreateCustomerAsync(client, city.CityId, classification.Id);
        var originalSuffix = created.Cuc[3..];

        var response = await client.PutAsJsonAsync(
            $"{CustomersUrl()}/{created.Id}",
            NewCustomerBody(city.CityId, newClassification.Id, name: created.Name),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<CustomerResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(updated);
        Assert.Equal(newClassification.Id, updated.Classification.Id);
        Assert.Equal($"GRA{originalSuffix}", updated.Cuc);
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
        var city = await EnsureCityAsync(mine);
        var classification = await CreateClassificationAsync(mine);
        var created = await CreateCustomerAsync(mine, city.CityId, classification.Id);

        var response = await theirs.PutAsJsonAsync(
            $"{CustomersUrl()}/{created.Id}",
            NewCustomerBody(city.CityId, classification.Id, name: "Robado"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
