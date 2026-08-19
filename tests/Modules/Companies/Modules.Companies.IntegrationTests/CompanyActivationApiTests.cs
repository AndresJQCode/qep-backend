using System.Net;
using System.Net.Http.Json;
using Modules.Companies.Application;
using static Modules.Companies.IntegrationTests.CompaniesApiHarness;

namespace Modules.Companies.IntegrationTests;

public sealed class CompanyActivationApiTests
{
    [Fact]
    public async Task DeactivateMarksTheCompanyInactive()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var created = await CreateCompanyAsync(client, "Andes Logistica S.A.S.", "CTA-000123");

        var response = await DeactivateAsync(client, created.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var company = await response.Content.ReadFromJsonAsync<CompanyResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(company);
        Assert.False(company.IsActive);
    }

    [Fact]
    public async Task DeactivateTwiceIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var created = await CreateCompanyAsync(client, "Andes Logistica S.A.S.", "CTA-000123");
        (await DeactivateAsync(client, created.Id)).EnsureSuccessStatusCode();

        var response = await DeactivateAsync(client, created.Id);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        Assert.Contains("companies.company.already_inactive", body, StringComparison.Ordinal);
    }

    // Sin este verbo una empresa inactiva seria terminal: el PUT abre con EnsureActive() y nada
    // devuelve IsActive a true. Es la falta que CAT-07 tuvo que corregir en producto despues.
    [Fact]
    public async Task ActivateBringsAnInactiveCompanyBack()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var created = await CreateCompanyAsync(client, "Andes Logistica S.A.S.", "CTA-000123");
        (await DeactivateAsync(client, created.Id)).EnsureSuccessStatusCode();

        var response = await ActivateAsync(client, created.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var company = await response.Content.ReadFromJsonAsync<CompanyResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(company);
        Assert.True(company.IsActive);
    }

    [Fact]
    public async Task ActivateAnAlreadyActiveCompanyIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var created = await CreateCompanyAsync(client, "Andes Logistica S.A.S.", "CTA-000123");

        var response = await ActivateAsync(client, created.Id);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        Assert.Contains("companies.company.already_active", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnInactiveCompanyCannotBeEdited()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var created = await CreateCompanyAsync(client, "Andes Logistica S.A.S.", "CTA-000123");
        (await DeactivateAsync(client, created.Id)).EnsureSuccessStatusCode();

        var response = await client.PutAsJsonAsync(
            $"{CompaniesUrl()}/{created.Id}",
            new
            {
                name = "Andes Logistica S.A.",
                bankAccounts = new[] { BankAccount("CTA-000123") },
                taxId = "900.111.222-3"
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        Assert.Contains("companies.company.inactive", body, StringComparison.Ordinal);
    }

    // Desde EMP-08 el numero de cuenta no es unico por tenant, asi que el estado de una empresa
    // no puede condicionar el alta de otra. Antes esto era un 422 —el indice unico lo prohibia
    // incluso con la primera inactiva—; ahora es un alta normal. La prueba se queda para que el
    // dia que alguien reintroduzca una unicidad global se ponga roja en vez de pasar en silencio.
    [Fact]
    public async Task DeactivatingDoesNotBlockAnotherCompanyFromUsingTheSameNumber()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var created = await CreateCompanyAsync(client, "Andes Logistica S.A.S.", "CTA-000123");
        (await DeactivateAsync(client, created.Id)).EnsureSuccessStatusCode();

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

    // Activar es administrar: no estrena permiso propio, pero tampoco lo alcanza el de lectura.
    [Fact]
    public async Task DeactivateWithOnlyTheReadPermissionIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var manager = CreateManager(factory);
        using var reader = CreateClient(
            factory, SubjectId, TenantId, CompaniesPermissions.CompanyRead);
        var created = await CreateCompanyAsync(manager, "Andes Logistica S.A.S.", "CTA-000123");

        var response = await DeactivateAsync(reader, created.Id);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task DeactivateAnUnknownCompanyIsNotFound()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);

        var response = await DeactivateAsync(client, Guid.CreateVersion7());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static Task<HttpResponseMessage> DeactivateAsync(HttpClient client, Guid companyId) =>
        client.PostAsync(
            $"{CompaniesUrl()}/{companyId}/deactivate",
            content: null,
            TestContext.Current.CancellationToken);

    private static Task<HttpResponseMessage> ActivateAsync(HttpClient client, Guid companyId) =>
        client.PostAsync(
            $"{CompaniesUrl()}/{companyId}/activate",
            content: null,
            TestContext.Current.CancellationToken);
}
