using System.Net;
using System.Net.Http.Json;
using Modules.Customers.Application;
using static Modules.Customers.IntegrationTests.CustomersApiHarness;

namespace Modules.Customers.IntegrationTests;

/// <summary>Spec 2026-11-10 contactos de empresa §4.1, §4.2 y §5 contra la base: el listado esconde a los contactos salvo que
/// haya una búsqueda o <c>contacts=include</c>, la razón social entra en <c>search</c> y <c>name</c>, y la ficha de la empresa trae
/// sus contactos. Un Postgres por prueba, como el resto del módulo.</summary>
public sealed class CustomerContactsListApiTests
{
    private sealed record Arranged(HttpClient Client, Guid CityId, Guid ClassificationId);

    private static async Task<Arranged> ArrangeAsync(QepApiFactory factory)
    {
        var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var classification = await CreateClassificationAsync(client);
        return new Arranged(client, city.CityId, classification.Id);
    }

    private static async Task<CustomerResponse> CreateAsync(
        Arranged arranged, string name, int number, string? businessName = null, string phone = "310 935 2187")
    {
        var response = await arranged.Client.PostAsJsonAsync(
            CustomersUrl(),
            new
            {
                name,
                businessName,
                identificationType = "CC",
                identificationNumber = $"1020304{number:D3}",
                phone,
                email = $"c{number}@verde.co",
                address = "Calle 10 # 45-12",
                country = "CO",
                cityId = arranged.CityId,
                classificationId = arranged.ClassificationId,
                withRetention = false,
                vatSurplus = false
            },
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<CustomerResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(created);
        return created;
    }

    private static async Task LinkAsync(HttpClient client, Guid contactId, Guid companyId) =>
        (await client.PutAsJsonAsync(
            $"{CustomersUrl()}/{contactId}/company", new { companyId }, TestContext.Current.CancellationToken))
        .EnsureSuccessStatusCode();

    private static async Task<(Arranged Arranged, CustomerResponse Company, CustomerResponse Contact)> CompanyWithContactAsync(
        QepApiFactory factory)
    {
        var arranged = await ArrangeAsync(factory);
        var company = await CreateAsync(arranged, "Contacto Acme", 1, businessName: "Acme Industrial S.A.S.", phone: "3101110001");
        var contact = await CreateAsync(arranged, "Laura Pérez", 2, phone: "3201112233");
        await LinkAsync(arranged.Client, contact.Id, company.Id);
        return (arranged, company, contact);
    }

    [Fact]
    public async Task ContactsAreHiddenByDefaultAndTheTotalAgrees()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (arranged, company, _) = await CompanyWithContactAsync(factory);
        using var client = arranged.Client;

        var page = await ListAsync(client, string.Empty);

        Assert.Equal(company.Id, Assert.Single(page.Items).Id);
        Assert.Equal(1, page.Total);
    }

    [Theory]
    [InlineData("?search=Laura")]
    [InlineData("?name=Laura")]
    [InlineData("?identificationNumber=1020304002")]
    [InlineData("?phone=3201112233")]
    public async Task ASearchBringsThemBack(string query)
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (arranged, _, contact) = await CompanyWithContactAsync(factory);
        using var client = arranged.Client;

        var page = await ListAsync(client, query);

        Assert.Equal(contact.Id, Assert.Single(page.Items).Id);
        Assert.Equal(1, page.Total);
    }

    [Fact]
    public async Task TheCucBoxBringsThemBack()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (arranged, _, contact) = await CompanyWithContactAsync(factory);
        using var client = arranged.Client;

        var page = await ListAsync(client, $"?cuc={Uri.EscapeDataString(contact.Cuc!)}");

        Assert.Equal(contact.Id, Assert.Single(page.Items).Id);
    }

    [Theory]
    [InlineData("?search=%20")]
    [InlineData("?name=")]
    [InlineData("?phone=abc")]
    public async Task ABlankSearchIsNotASearchAndTheContactsStayHidden(string query)
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (arranged, company, _) = await CompanyWithContactAsync(factory);
        using var client = arranged.Client;

        var page = await ListAsync(client, query);

        Assert.Equal(company.Id, Assert.Single(page.Items).Id);
        Assert.Equal(1, page.Total);
    }

    [Fact]
    public async Task ContactsExcludeWinsOverASearch()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (arranged, _, _) = await CompanyWithContactAsync(factory);
        using var client = arranged.Client;

        var page = await ListAsync(client, "?search=Laura&contacts=exclude");

        Assert.Empty(page.Items);
        Assert.Equal(0, page.Total);
    }

    [Fact]
    public async Task ContactsIncludeShowsThemWithoutASearch()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (arranged, company, contact) = await CompanyWithContactAsync(factory);
        using var client = arranged.Client;

        var page = await ListAsync(client, "?contacts=INCLUDE");

        Assert.Equal(2, page.Total);
        Assert.Contains(page.Items, item => item.Id == company.Id);
        Assert.Contains(page.Items, item => item.Id == contact.Id);
    }

    [Fact]
    public async Task AnUnknownContactsValueIs422WithItsKey()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (arranged, _, _) = await CompanyWithContactAsync(factory);
        using var client = arranged.Client;

        var response = await client.GetAsync($"{CustomersUrl()}?contacts=all", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("contacts", await ValidationFieldsAsync(response), StringComparer.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("?search=acme")]
    [InlineData("?name=acme")]
    public async Task SearchFindsACompanyByItsBusinessName(string query)
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (arranged, company, _) = await CompanyWithContactAsync(factory);
        using var client = arranged.Client;

        var page = await ListAsync(client, query);

        // La razón social no es el nombre del cliente (`Contacto Acme` también lo contiene: por eso el nombre buscado es
        // «industrial», que sólo está en la razón social).
        Assert.Equal(company.Id, Assert.Single(page.Items).Id);
        var onlyBusinessName = await ListAsync(client, query.Replace("acme", "industrial", StringComparison.Ordinal));
        Assert.Equal(company.Id, Assert.Single(onlyBusinessName.Items).Id);
    }

    [Fact]
    public async Task ItemsCarryBusinessNamePersonTypeCompanyAndContactCount()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (arranged, company, contact) = await CompanyWithContactAsync(factory);
        using var client = arranged.Client;

        var page = await ListAsync(client, "?contacts=include");

        var companyItem = page.Items.Single(item => item.Id == company.Id);
        var contactItem = page.Items.Single(item => item.Id == contact.Id);
        Assert.Equal("Acme Industrial S.A.S.", companyItem.BusinessName);
        Assert.Equal(1, companyItem.ContactCount);
        Assert.Null(companyItem.Company);
        Assert.Equal(0, contactItem.ContactCount);
        Assert.Equal(company.Id, contactItem.Company!.Id);
        Assert.Equal("Acme Industrial S.A.S.", contactItem.Company.Name);
        Assert.True(contactItem.Company.IsActive);
    }

    [Fact]
    public async Task TheCompanyDetailCarriesItsContactsOrderedByNameAndAContactCarriesItsCompany()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (arranged, company, contact) = await CompanyWithContactAsync(factory);
        using var client = arranged.Client;
        var first = await CreateAsync(arranged, "Ana Gómez", 3);
        await LinkAsync(client, first.Id, company.Id);

        var detail = await GetAsync(client, company.Id);
        var contactDetail = await GetAsync(client, contact.Id);

        Assert.Equal(2, detail.ContactCount);
        Assert.Equal(["Ana Gómez", "Laura Pérez"], detail.Contacts!.Select(item => item.Name));
        Assert.Equal(company.Id, contactDetail.Company!.Id);
        Assert.Empty(contactDetail.Contacts!);
        Assert.Equal(0, contactDetail.ContactCount);
    }

    [Fact]
    public async Task AWriteAnsweringTheCompanyKeepsItsContacts()
    {
        // Ruling P4: la respuesta de una escritura sobre la empresa trae lo mismo que un GET.
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (arranged, company, _) = await CompanyWithContactAsync(factory);
        using var client = arranged.Client;

        var response = await client.PostAsync(
            $"{CustomersUrl()}/{company.Id}/deactivate", content: null, TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<CustomerResponse>(TestContext.Current.CancellationToken);
        Assert.Equal(1, body!.ContactCount);
        Assert.Single(body.Contacts!);
    }

    [Fact]
    public async Task TheDetailCapsContactsAndStillCountsThemAll()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var arranged = await ArrangeAsync(factory);
        using var client = arranged.Client;
        var company = await CreateAsync(arranged, "Verde Esencial", 1);
        for (var index = 0; index < CustomerContactsLimits.MaxInDetail + 1; index++)
        {
            var contact = await CreateAsync(arranged, $"Contacto {index:D3}", 10 + index);
            await LinkAsync(client, contact.Id, company.Id);
        }

        var detail = await GetAsync(client, company.Id);

        Assert.Equal(CustomerContactsLimits.MaxInDetail, detail.Contacts!.Count);
        Assert.Equal(CustomerContactsLimits.MaxInDetail + 1, detail.ContactCount);
    }

    [Fact]
    public async Task ThePageResolvesEveryRowsCompanyAndCountWhateverItsSize()
    {
        // No hay forma de contar las consultas desde aquí; lo observable es que, con 20 filas, cada una trae su empresa y su
        // conteo correctos. Que la resolución sea una llamada en lote por página lo prueba ListCustomersContactsTests.
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var arranged = await ArrangeAsync(factory);
        using var client = arranged.Client;
        var company = await CreateAsync(arranged, "Verde Esencial", 1);
        var contacts = new List<CustomerResponse>();
        for (var index = 0; index < 20; index++)
        {
            var contact = await CreateAsync(arranged, $"Contacto {index:D2}", 10 + index);
            await LinkAsync(client, contact.Id, company.Id);
            contacts.Add(contact);
        }

        var page = await ListAsync(client, "?contacts=include&pageSize=50");

        Assert.Equal(21, page.Items.Count);
        Assert.Equal(20, page.Items.Single(item => item.Id == company.Id).ContactCount);
        Assert.All(
            contacts,
            contact => Assert.Equal(company.Id, page.Items.Single(item => item.Id == contact.Id).Company!.Id));
    }
}
