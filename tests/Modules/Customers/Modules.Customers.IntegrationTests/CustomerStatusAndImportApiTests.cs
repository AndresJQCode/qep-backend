using System.Net;
using System.Net.Http.Json;
using System.Text;
using Modules.Customers.Application;
using static Modules.Customers.IntegrationTests.CustomersApiHarness;

namespace Modules.Customers.IntegrationTests;

public sealed class CustomerStatusAndImportApiTests
{
    private static Task<HttpResponseMessage> DeactivateAsync(HttpClient client, Guid id) =>
        client.PostAsync(
            $"{CustomersUrl()}/{id}/deactivate",
            content: null,
            TestContext.Current.CancellationToken);

    private static Task<HttpResponseMessage> ActivateAsync(HttpClient client, Guid id) =>
        client.PostAsync(
            $"{CustomersUrl()}/{id}/activate",
            content: null,
            TestContext.Current.CancellationToken);

    [Fact]
    public async Task DeactivateReturnsTheUpdatedCustomer()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var created = await CreateCustomerAsync(client);

        var response = await DeactivateAsync(client, created.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var customer = await response.Content.ReadFromJsonAsync<CustomerResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(customer);
        Assert.False(customer.IsActive);
    }

    [Fact]
    public async Task DeactivatingTwiceIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var created = await CreateCustomerAsync(client);
        (await DeactivateAsync(client, created.Id)).EnsureSuccessStatusCode();

        var response = await DeactivateAsync(client, created.Id);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        Assert.Contains("customers.customer.already_inactive", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnInactiveCustomerCannotBeEdited()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var created = await CreateCustomerAsync(client);
        (await DeactivateAsync(client, created.Id)).EnsureSuccessStatusCode();

        var response = await client.PutAsJsonAsync(
            $"{CustomersUrl()}/{created.Id}",
            NewCustomerBody(name: "Otro nombre"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        Assert.Contains("customers.customer.inactive", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// Sin <c>/activate</c>, inactivar seria irreversible: el PUT abre con <c>EnsureActive</c> y
    /// nada devolveria <c>IsActive</c> a true. `CLI-01` no lista el verbo — existe porque es la
    /// falta que `CAT-07` tuvo que corregir en producto despues de entregarlo.
    /// </summary>
    [Fact]
    public async Task ActivateRestoresEditability()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var created = await CreateCustomerAsync(client);
        (await DeactivateAsync(client, created.Id)).EnsureSuccessStatusCode();

        var activated = await ActivateAsync(client, created.Id);
        Assert.Equal(HttpStatusCode.OK, activated.StatusCode);

        var response = await client.PutAsJsonAsync(
            $"{CustomersUrl()}/{created.Id}",
            NewCustomerBody(name: "Verde Esencial S.A."),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // Inactivar no libera el documento: IX_customers_tenant_identification es unico **sin filtro
    // parcial**. De eso depende que reactivar no tenga que revalidar unicidad; si alguien le agrega
    // un filtro parcial al indice, esta prueba avisa.
    [Fact]
    public async Task DeactivatingDoesNotFreeTheIdentification()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var created = await CreateCustomerAsync(client);
        (await DeactivateAsync(client, created.Id)).EnsureSuccessStatusCode();

        var response = await client.PostAsJsonAsync(
            CustomersUrl(),
            NewCustomerBody(name: "Otro Cliente"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    // Activar es administrar: no estrena permiso propio, pero tampoco lo alcanza el de lectura.
    [Fact]
    public async Task DeactivateWithOnlyTheReadPermissionIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var manager = CreateManager(factory);
        using var reader = CreateClient(
            factory, SubjectId, TenantId, CustomersPermissions.CustomerRead);
        var created = await CreateCustomerAsync(manager);

        var response = await DeactivateAsync(reader, created.Id);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static MultipartFormDataContent ExcelUpload(
        string fileName,
        int sizeInBytes = 32)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(new string('x', sizeInBytes)));
        content.Add(file, "file", fileName);
        return content;
    }

    /// <summary>
    /// La importacion responde **202 y no 201**: acepta el archivo, no crea clientes. `CLI-01`
    /// deja el procesamiento del Excel fuera de alcance y `SDD-OD-10` sigue abierta.
    /// </summary>
    [Fact]
    public async Task ImportAcceptsAnExcelFileWithoutCreatingCustomers()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateImporter(factory);

        using var upload = ExcelUpload("clientes.xlsx");
        var response = await client.PostAsync(
            $"{CustomersUrl()}/import", upload, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ImportCustomersResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.Equal("clientes.xlsx", result.FileName);
        Assert.Equal("accepted", result.Status);

        // Y el listado sigue vacio, que es la mitad del contrato que este slice promete.
        var page = await ListAsync(client, string.Empty);
        Assert.Empty(page.Items);
    }

    [Theory]
    [InlineData("clientes.csv")]
    [InlineData("clientes.pdf")]
    [InlineData("clientes")]
    public async Task ImportRejectsAFileThatIsNotExcel(string fileName)
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateImporter(factory);

        using var upload = ExcelUpload(fileName);
        var response = await client.PostAsync(
            $"{CustomersUrl()}/import", upload, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        Assert.Contains("customers.import.file_type_invalid", body, StringComparison.Ordinal);
    }

    // El permiso de importar es propio y **no** lo cubre `manage`: mil clientes de una vez no es la
    // misma autoridad que editar uno, y el gate CLI-00 pide mapearlo por separado.
    [Fact]
    public async Task ImportWithOnlyTheManagePermissionIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);

        using var upload = ExcelUpload("clientes.xlsx");
        var response = await client.PostAsync(
            $"{CustomersUrl()}/import", upload, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
