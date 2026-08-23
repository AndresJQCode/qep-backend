using System.Net;
using System.Net.Http.Json;
using Modules.Customers.Application;
using static Modules.Customers.IntegrationTests.CustomersApiHarness;

namespace Modules.Customers.IntegrationTests;

public sealed class CustomerApiTests
{
    [Fact]
    public async Task ListReturnsTheEnvelopeWithItemsAndTotal()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var classification = await CreateClassificationAsync(client);
        await CreateCustomerAsync(
            client, city.CityId, classification.Id, "Verde Esencial S.A.S.", "900.111.111-1");
        await CreateCustomerAsync(
            client, city.CityId, classification.Id, "Naturaleza Viva Ltda.", "830.222.222-2");

        var page = await ListAsync(client, string.Empty);

        Assert.Equal(2, page.Total);
        Assert.Equal(2, page.Items.Count);
        Assert.Equal(1, page.Page);
    }

    // La ciudad y la clasificacion viajan resueltas en cada fila del listado (Fase 3), no solo en
    // el detalle: la grilla las pinta sin pedir un GET por cliente.
    [Fact]
    public async Task ListResolvesTheCityAndTheClassificationOfEachItem()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var classification = await CreateClassificationAsync(client, "Mediano", "CLI");
        await CreateCustomerAsync(client, city.CityId, classification.Id);

        var page = await ListAsync(client, string.Empty);

        var item = Assert.Single(page.Items);
        Assert.Equal(city.CityId, item.City.Id);
        Assert.Equal(classification.Id, item.Classification.Id);
        Assert.Equal(classification.Prefix, item.Classification.Prefix);
    }

    // Con clientes en ciudades y clasificaciones distintas, el listado tiene que resolver cada uno
    // sin un N+1: esta prueba no lo mide directamente (esa es la responsabilidad de
    // ListCustomersHandler.ToDtosAsync, en lote), pero si confirma que cada fila trae los datos
    // correctos de SU cliente y no los del ultimo resuelto.
    [Fact]
    public async Task ListResolvesDifferentCitiesAndClassificationsCorrectlyPerItem()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var retail = await CreateClassificationAsync(client, "Minorista", "MIN");
        var wholesale = await CreateClassificationAsync(client, "Mayorista", "MAY");
        await CreateCustomerAsync(
            client, city.CityId, retail.Id, "Verde Esencial", "900.111.111-1");
        await CreateCustomerAsync(
            client, city.CityId, wholesale.Id, "Naturaleza Viva", "830.222.222-2");

        var page = await ListAsync(client, string.Empty);

        var verde = Assert.Single(page.Items, item => item.Name == "Verde Esencial");
        var naturaleza = Assert.Single(page.Items, item => item.Name == "Naturaleza Viva");
        Assert.Equal(retail.Id, verde.Classification.Id);
        Assert.Equal(wholesale.Id, naturaleza.Classification.Id);
    }

    // La caja del listado busca por nombre, identificacion y CUC — literalmente lo que dice su
    // placeholder: "Buscar por nombre, identificación o CUC".
    [Fact]
    public async Task ListMatchesByNameIdentificationAndCuc()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var classification = await CreateClassificationAsync(client);
        var first = await CreateCustomerAsync(
            client, city.CityId, classification.Id, "Verde Esencial S.A.S.", "900.111.111-1");
        await CreateCustomerAsync(
            client, city.CityId, classification.Id, "Naturaleza Viva Ltda.", "830.222.222-2");

        var byName = await ListAsync(client, "?search=esencial");
        var byIdentification = await ListAsync(client, "?search=830.222");
        var byCuc = await ListAsync(client, $"?search={first.Cuc}");

        Assert.Equal("Verde Esencial S.A.S.", Assert.Single(byName.Items).Name);
        Assert.Equal("Naturaleza Viva Ltda.", Assert.Single(byIdentification.Items).Name);
        Assert.Equal(first.Cuc, Assert.Single(byCuc.Items).Cuc);
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
        var city = await EnsureCityAsync(client);
        var classification = await CreateClassificationAsync(client);
        await CreateCustomerAsync(
            client, city.CityId, classification.Id, "Verde Esencial S.A.S.", "900.111.111-1");

        var underscore = await ListAsync(client, "?search=_");
        var percent = await ListAsync(client, "?search=%25");

        Assert.Empty(underscore.Items);
        Assert.Empty(percent.Items);
    }

    /// <summary>
    /// El total cuenta las coincidencias, no la pagina.
    ///
    /// Contarlo despues del <c>Skip</c> devolveria como mucho <c>pageSize</c> y la UI dibujaria
    /// una sola pagina siempre — el listado se veria completo con la mitad de los clientes
    /// escondidos.
    /// </summary>
    [Fact]
    public async Task ListPaginatesAndCountsEveryMatch()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var classification = await CreateClassificationAsync(client);
        for (var index = 1; index <= 5; index++)
        {
            await CreateCustomerAsync(
                client, city.CityId, classification.Id, $"Cliente {index:D2}", $"900.000.00{index}-1");
        }

        var first = await ListAsync(client, "?page=1&pageSize=2");
        var third = await ListAsync(client, "?page=3&pageSize=2");

        Assert.Equal(5, first.Total);
        Assert.Equal(2, first.Items.Count);
        Assert.Equal("Cliente 01", first.Items.First().Name);
        Assert.Equal(5, third.Total);
        Assert.Equal("Cliente 05", Assert.Single(third.Items).Name);
    }

    // Un pageSize desmedido se recorta al tope en vez de fallar, y la respuesta lleva el valor real
    // para que el llamador lo vea. Sin tope, `?pageSize=1000000` se traduce en traerse el tenant
    // entero a memoria: un DoS que se escribe desde la barra de direcciones.
    [Fact]
    public async Task ListClampsAnOversizedPageSize()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var classification = await CreateClassificationAsync(client);
        await CreateCustomerAsync(client, city.CityId, classification.Id);

        var page = await ListAsync(client, "?pageSize=1000000");

        Assert.Equal(CustomerPaging.MaxPageSize, page.PageSize);
    }

    [Fact]
    public async Task ListWithoutTheReadPermissionIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId);

        var response = await client.GetAsync(
            CustomersUrl(), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // El listado de otro tenant no se alcanza ni con el permiso puesto.
    [Fact]
    public async Task ListForAnotherTenantIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(
            factory, SubjectId, TenantId, CustomersPermissions.CustomerRead);

        var response = await client.GetAsync(
            CustomersUrl(OtherTenantId), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetReturnsTheDetail()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var classification = await CreateClassificationAsync(client);
        var created = await CreateCustomerAsync(client, city.CityId, classification.Id);

        var customer = await client.GetFromJsonAsync<CustomerResponse>(
            $"{CustomersUrl()}/{created.Id}",
            TestContext.Current.CancellationToken);

        Assert.NotNull(customer);
        Assert.Equal(created.Id, customer.Id);
        Assert.Equal("NIT", customer.IdentificationType);
        Assert.Equal("900.123.456-1", customer.IdentificationNumber);
    }

    // El detalle trae la ciudad, el departamento y la clasificacion resueltos — no solo sus ids.
    [Fact]
    public async Task GetResolvesTheCityDepartmentAndClassification()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var classification = await CreateClassificationAsync(client, "Mediano", "CLI");
        var created = await CreateCustomerAsync(client, city.CityId, classification.Id);

        var customer = await client.GetFromJsonAsync<CustomerResponse>(
            $"{CustomersUrl()}/{created.Id}",
            TestContext.Current.CancellationToken);

        Assert.NotNull(customer);
        Assert.Equal(city.CityId, customer.City.Id);
        Assert.Equal(city.DepartmentDivipolaCode, customer.Department.DivipolaCode);
        Assert.Equal(classification.Id, customer.Classification.Id);
        Assert.Equal(classification.Name, customer.Classification.Name);
        Assert.Equal(classification.Prefix, customer.Classification.Prefix);
    }

    // El 404 lleva su codigo de dominio. Sin el, el consumidor tiene que adivinar por el status y
    // cualquier cambio de mensaje le rompe la pantalla en silencio.
    [Fact]
    public async Task GetAnUnknownCustomerIsNotFoundWithItsDomainCode()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);

        var response = await client.GetAsync(
            $"{CustomersUrl()}/{Guid.CreateVersion7()}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        Assert.Contains("customers.customer.not_found", body, StringComparison.Ordinal);
    }
}
