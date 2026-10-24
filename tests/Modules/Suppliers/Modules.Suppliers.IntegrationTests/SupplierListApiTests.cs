using System.Net;
using System.Net.Http.Json;
using Modules.Suppliers.Application;
using static Modules.Suppliers.IntegrationTests.SuppliersApiHarness;

namespace Modules.Suppliers.IntegrationTests;

public sealed class SupplierListApiTests
{
    [Fact]
    public async Task ListIsPagedWithTotalAndOrderedByName()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);
        await CreateNamedAsync(client, "Charlie", "1");
        await CreateNamedAsync(client, "Alfa", "2");
        await CreateNamedAsync(client, "Bravo", "3");

        var first = await ListAsync(client, "?pageSize=2");
        var second = await ListAsync(client, "?pageSize=2&page=2");

        Assert.Equal(2, first.Items.Count);
        Assert.Equal(3, first.Total);
        Assert.Equal(1, first.Page);
        Assert.Equal(["Alfa", "Bravo"], first.Items.Select(item => item.Name));
        Assert.Single(second.Items);
        Assert.Equal("Charlie", second.Items.Single().Name);
        Assert.Equal(2, second.Page);
    }

    [Fact]
    public async Task ListFiltersBySearchOverNameBusinessNameAndDocument()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);
        await CreateNamedAsync(client, "Pernos del Norte", "1");
        await CreateSupplierAsync(client, new()
        {
            ["name"] = "Tuercas",
            ["businessName"] = "Razon Zafiro Ltda",
            ["identificationNumber"] = "2"
        });
        await CreateSupplierAsync(client, new()
        {
            ["name"] = "Arandelas",
            ["identificationNumber"] = "777888"
        });
        await CreateNamedAsync(client, "100% Clavos", "4");

        Assert.Equal(
            ["Pernos del Norte"], (await ListAsync(client, "?search=norte")).Items.Select(item => item.Name));
        Assert.Equal(
            ["Tuercas"], (await ListAsync(client, "?search=zafiro")).Items.Select(item => item.Name));
        Assert.Equal(
            ["Arandelas"], (await ListAsync(client, "?search=777888")).Items.Select(item => item.Name));

        // El "%" se escapa: buscar "%" encuentra sólo el que lo trae literal, no todos.
        Assert.Equal(
            ["100% Clavos"], (await ListAsync(client, "?search=%25")).Items.Select(item => item.Name));
    }

    [Fact]
    public async Task ListFiltersByStatusAndRejectsAnUnknownValue()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);
        await CreateNamedAsync(client, "Activo", "1");
        var inactive = await CreateNamedAsync(client, "Inactivo", "2");
        (await client.PostAsync(
            $"{SuppliersUrl()}/{inactive.Id}/deactivate",
            content: null,
            TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        Assert.Equal(["Activo"], (await ListAsync(client, "?status=active")).Items.Select(item => item.Name));
        Assert.Equal(["Inactivo"], (await ListAsync(client, "?status=inactive")).Items.Select(item => item.Name));
        Assert.Equal(2, (await ListAsync(client)).Total);

        var response = await client.GetAsync(
            $"{SuppliersUrl()}?status=paused", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains(
            "suppliers.supplier.status_filter_invalid", await BodyAsync(response), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListFiltersByCategoryIgnoringCase()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);
        await CreateSupplierAsync(client, new()
        {
            ["name"] = "Uno",
            ["identificationNumber"] = "1",
            ["category"] = "Ferretería"
        });
        await CreateSupplierAsync(client, new()
        {
            ["name"] = "Dos",
            ["identificationNumber"] = "2",
            ["category"] = "Papelería"
        });

        var page = await ListAsync(client, "?category=ferretería");

        Assert.Equal(["Uno"], page.Items.Select(item => item.Name));
    }

    [Fact]
    public async Task ListCapsPageSizeAt200AndDefaultsTo50()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);

        Assert.Equal(200, (await ListAsync(client, "?pageSize=999")).PageSize);
        Assert.Equal(50, (await ListAsync(client)).PageSize);
    }

    [Fact]
    public async Task ListItemCarriesCityNameAndBankAccountsWithoutCurrency()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);
        var colombian = await CreateNamedAsync(client, "Colombiano", "1");
        await CreateSupplierAsync(client, new()
        {
            ["name"] = "Extranjero",
            ["identificationNumber"] = "2",
            ["country"] = "US",
            ["cityId"] = null,
            ["cityName"] = "Miami",
            ["phone"] = null
        });

        var page = await ListAsync(client);

        var local = page.Items.Single(item => item.Name == "Colombiano");
        Assert.Equal(colombian.City!.Name, local.City);
        Assert.Equal(
            [("Bancolombia", "123-456"), ("Davivienda", "789-012")],
            local.BankAccounts.Select(account => (account.BankName, account.AccountNumber)));
        Assert.Equal("Miami", page.Items.Single(item => item.Name == "Extranjero").City);

        // El contrato de la grilla no lleva moneda por cuenta.
        var raw = await client.GetStringAsync(SuppliersUrl(), TestContext.Current.CancellationToken);
        Assert.DoesNotContain("currency", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CategoriesReturnsDistinctSortedValuesInUse()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);
        await CreateSupplierAsync(client, new() { ["identificationNumber"] = "1", ["category"] = "Papelería" });
        await CreateSupplierAsync(client, new() { ["identificationNumber"] = "2", ["category"] = "Ferretería" });
        await CreateSupplierAsync(client, new() { ["identificationNumber"] = "3", ["category"] = "Papelería" });
        await CreateSupplierAsync(client, new() { ["identificationNumber"] = "4", ["category"] = null });

        var categories = await client.GetFromJsonAsync<SupplierCategoriesResponse>(
            $"{SuppliersUrl()}/categories", TestContext.Current.CancellationToken);

        Assert.NotNull(categories);
        Assert.Equal(["Ferretería", "Papelería"], categories.Items);
    }

    // El filtro por categoría ignora mayúsculas; el listado de categorías no puede mostrar
    // «Ferretería» y «ferretería» como dos opciones que filtran lo mismo.
    [Fact]
    public async Task CategoriesCollapsesCasingVariantsIntoOneItem()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);
        await CreateSupplierAsync(client, new() { ["identificationNumber"] = "1", ["category"] = "Ferretería" });
        await CreateSupplierAsync(client, new() { ["identificationNumber"] = "2", ["category"] = "ferretería" });
        await CreateSupplierAsync(client, new() { ["identificationNumber"] = "3", ["category"] = "Papelería" });

        var categories = await client.GetFromJsonAsync<SupplierCategoriesResponse>(
            $"{SuppliersUrl()}/categories", TestContext.Current.CancellationToken);

        Assert.NotNull(categories);
        Assert.Equal(2, categories.Items.Count);
        Assert.Single(categories.Items, item => string.Equals(item, "Ferretería", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Papelería", categories.Items);
    }

    private static Task<SupplierResponse> CreateNamedAsync(HttpClient client, string name, string document) =>
        CreateSupplierAsync(client, new() { ["name"] = name, ["identificationNumber"] = document });

    private static async Task<SuppliersResponse> ListAsync(HttpClient client, string query = "")
    {
        var page = await client.GetFromJsonAsync<SuppliersResponse>(
            $"{SuppliersUrl()}{query}", TestContext.Current.CancellationToken);
        Assert.NotNull(page);
        return page;
    }
}
