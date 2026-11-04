using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Modules.Store.IntegrationTests;

/// <summary>
/// Una tienda publicada lista para leer como lo hace el storefront: con la clave compartida y el host de la ruta. Se apoya en
/// <see cref="StoreSettingsFixture"/> (empresa con cuentas en COP y EUR, producto <c>CAM-001</c> sin publicar en la tienda) y le suma
/// productos y categorías sembrados por la API de Catalog.
/// </summary>
internal sealed class PublicStoreFixture : IAsyncDisposable
{
    // Es el secreto de juguete que fija StoreApiHarness.QepApiFactory.
    private const string StorefrontKey = "integration-tests-storefront-secret-0123456789";

    public const string Host = "tienda-demo.lulocrm.com";

    private static readonly string[] Sizes = ["M", "L"];

    private PublicStoreFixture(StoreSettingsFixture settings, HttpClient storefront)
    {
        Settings = settings;
        Storefront = storefront;
    }

    public StoreSettingsFixture Settings { get; }

    /// <summary>El storefront: la clave en el header y ningún otro dato de sesión.</summary>
    public HttpClient Storefront { get; }

    public static Guid Tenant => StoreSettingsFixture.Tenant;

    /// <param name="tweak">Pisa campos del cuerpo del <c>PUT</c> de ajustes antes de guardar.</param>
    /// <param name="publish">Falso deja la tienda guardada pero sin publicar.</param>
    public static async Task<PublicStoreFixture> OpenAsync(
        Action<StoreSettingsFixture, Dictionary<string, object?>>? tweak = null,
        bool publish = true,
        IEnumerable<Modules.Tenancy.Domain.TenantModuleKey>? modules = null,
        Action<Microsoft.Extensions.DependencyInjection.IServiceCollection>? configureServices = null)
    {
        var settings = await StoreSettingsFixture.OpenAsync(modules, configureServices);
        var body = settings.Body();
        tweak?.Invoke(settings, body);
        await settings.SaveAsync(body);
        if (publish)
        {
            var published = await settings.PostAsync("/publish", ifMatch: "1");
            Assert.Equal(System.Net.HttpStatusCode.OK, published.StatusCode);
        }

        var storefront = settings.Factory.CreateClient();
        storefront.DefaultRequestHeaders.Add("X-Qep-Storefront-Key", StorefrontKey);
        return new PublicStoreFixture(settings, storefront);
    }

    public static string Url(string path, string host = Host) => $"/api/v1/public/stores/{host}{path}";

    public Task<HttpResponseMessage> GetAsync(string path, string host = Host) =>
        Storefront.GetAsync(Url(path, host), TestContext.Current.CancellationToken);

    public Task<HttpResponseMessage> QuoteAsync(object body, string host = Host) =>
        Storefront.PostAsJsonAsync(Url("/cart/quote", host), body, TestContext.Current.CancellationToken);

    public static async Task<JsonNode> JsonAsync(HttpResponseMessage response) =>
        JsonNode.Parse(await StoreApiHarness.BodyAsync(response))!;

    public async Task<JsonNode> GetJsonAsync(string path, string host = Host)
    {
        var response = await GetAsync(path, host);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        return await JsonAsync(response);
    }

    public static object NoDiscountScales() =>
        new[] { new { fromUnit = 1, toUnit = 999_999, discount = 0m, restriction = "multiple", multiple = 1, allowGrouping = false } };

    /// <summary>Un producto de una variante, visible en la tienda por defecto. Devuelve el id del producto y el de su variante.</summary>
    public async Task<(Guid ProductId, Guid VariantId)> CreateProductAsync(
        string name,
        string code,
        IReadOnlyDictionary<string, decimal> prices,
        bool visible = true,
        Guid? categoryId = null,
        object? scales = null,
        string? barcode = null)
    {
        var response = await Settings.Admin.PostAsJsonAsync(
            $"/api/v1/tenants/{Tenant}/catalog/products",
            new
            {
                name,
                categoryId,
                visibleInStore = visible,
                variants = new[]
                {
                    new
                    {
                        code,
                        barcode,
                        pricing = new { prices, scales = scales ?? NoDiscountScales(), packagingUnits = Array.Empty<int>() },
                    },
                },
            },
            TestContext.Current.CancellationToken);
        var json = await JsonAsync(response);
        Assert.True(response.IsSuccessStatusCode, json.ToJsonString());
        return ((Guid)json["id"]!, (Guid)json["variants"]![0]!["id"]!);
    }

    /// <summary>«Camiseta» con la talla M y la L, a 30.000 y 32.000 COP, con las escalas dadas en las dos.</summary>
    public async Task<(Guid ProductId, Guid M, Guid L)> CreateShirtInTwoSizesAsync(object scales)
    {
        var optionResponse = await Settings.Admin.PostAsJsonAsync(
            $"/api/v1/tenants/{Tenant}/catalog/options",
            new { name = "Talla", values = Sizes },
            TestContext.Current.CancellationToken);
        var option = await JsonAsync(optionResponse);
        Assert.True(optionResponse.IsSuccessStatusCode, option.ToJsonString());
        var values = option["values"]!.AsArray();
        var valueM = (Guid)values.Single(value => (string)value!["name"]! == "M")!["id"]!;
        var valueL = (Guid)values.Single(value => (string)value!["name"]! == "L")!["id"]!;

        object Variant(string code, Guid valueId, decimal cop) => new
        {
            code,
            optionValueIds = new[] { valueId },
            pricing = new
            {
                prices = new Dictionary<string, decimal> { ["COP"] = cop },
                scales,
                packagingUnits = Array.Empty<int>(),
            },
        };

        var response = await Settings.Admin.PostAsJsonAsync(
            $"/api/v1/tenants/{Tenant}/catalog/products",
            new
            {
                name = "Camiseta polo",
                optionIds = new[] { (Guid)option["id"]! },
                visibleInStore = true,
                variants = new[] { Variant("POLO-M", valueM, 30_000m), Variant("POLO-L", valueL, 32_000m) },
            },
            TestContext.Current.CancellationToken);
        var json = await JsonAsync(response);
        Assert.True(response.IsSuccessStatusCode, json.ToJsonString());
        var variants = json["variants"]!.AsArray();
        return (
            (Guid)json["id"]!,
            (Guid)variants.Single(variant => (string)variant!["code"]! == "POLO-M")!["id"]!,
            (Guid)variants.Single(variant => (string)variant!["code"]! == "POLO-L")!["id"]!);
    }

    public async Task<Guid> CreateCategoryAsync(string name, Guid? parentId = null)
    {
        var response = await Settings.Admin.PostAsJsonAsync(
            $"/api/v1/tenants/{Tenant}/catalog/categories", new { name, parentId }, TestContext.Current.CancellationToken);
        var json = await JsonAsync(response);
        Assert.True(response.IsSuccessStatusCode, json.ToJsonString());
        return (Guid)json["id"]!;
    }

    public static IReadOnlyDictionary<string, decimal> Prices(decimal cop, decimal? eur = null) =>
        eur is null
            ? new Dictionary<string, decimal> { ["COP"] = cop }
            : new Dictionary<string, decimal> { ["COP"] = cop, ["EUR"] = eur.Value };

    /// <summary>Hace que la tienda venda COP y EUR (con EUR de principal), cada una con su cuenta de cobro.</summary>
    public static void TwoCurrencies(StoreSettingsFixture fixture, Dictionary<string, object?> body)
    {
        body["currencies"] = new[]
        {
            fixture.CurrencyEntry("COP", "Bancolombia", "A-COP-1"),
            fixture.CurrencyEntry("EUR", "Davivienda", "B-EUR-1"),
        };
        body["primaryCurrency"] = "EUR";
    }

    /// <summary>Un <c>UPDATE</c> directo con un único parámetro <c>@id</c>: para estados que la API no deja armar en un paso.</summary>
    public async Task ExecuteAsync(string sql, Guid id)
    {
        var connectionString = Settings.Factory.Services
            .GetRequiredService<IConfiguration>()
            .GetConnectionString("QepDatabase")!;
        await using var connection = new Npgsql.NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new Npgsql.NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        Assert.Equal(1, await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
    }

    public async ValueTask DisposeAsync()
    {
        Storefront.Dispose();
        await Settings.DisposeAsync();
    }
}
