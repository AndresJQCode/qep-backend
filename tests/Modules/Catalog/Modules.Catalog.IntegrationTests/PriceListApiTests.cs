using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Modules.Catalog.Application;
using Modules.Catalog.Domain;
using static Modules.Catalog.IntegrationTests.CatalogTestDatabase;

namespace Modules.Catalog.IntegrationTests;

/// <summary>
/// Spec 2026-11-22 (listas de precios por canal): las rutas de listas, precios fijos y «Precios por canal» contra Postgres, y el
/// resolutor de precios de canal (D-2, D-12) sobre lo que esas rutas guardaron.
/// </summary>
public sealed class PriceListApiTests(SharedCatalogHost sharedHost) : IClassFixture<SharedCatalogHost>
{
    private const string SubjectId = "01900000-0000-7000-8000-000000000002";

    private static readonly string[] ManagePermissions =
        [CatalogPermissions.ProductRead, CatalogPermissions.ProductManage, CatalogPermissions.PriceListManage];

    private string TenantId { get; } = Guid.CreateVersion7().ToString();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string ListsUrl => $"/api/v1/tenants/{TenantId}/catalog/price-lists";

    private string ChannelsUrl => $"/api/v1/tenants/{TenantId}/catalog/channel-prices";

    [Fact]
    public async Task CreateReturns201WithTheSignedAdjustmentAndTheList()
    {
        using var client = await ManagerAsync();

        var response = await client.PostAsJsonAsync(
            ListsUrl, new { name = "  Tienda online  ", adjustmentPercent = 12.5m, visibility = "AllActive" }, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var detail = (await response.Content.ReadFromJsonAsync<PriceListDetailDto>(Ct))!;
        Assert.Equal($"{ListsUrl}/{detail.Id}", response.Headers.Location?.OriginalString);
        Assert.Equal(("Tienda online", 12.5m, "AllActive", true, 0, 1L),
            (detail.Name, detail.AdjustmentPercent, detail.Visibility, detail.ShowPrices, detail.OverrideCount, detail.Version));
        Assert.Empty(detail.Channels);

        var listed = (await client.GetFromJsonAsync<PriceListsResponse>(ListsUrl, Ct))!;
        Assert.Equal([("Tienda online", 12.5m)], listed.Items.Select(item => (item.Name, item.AdjustmentPercent)));
    }

    [Theory]
    [InlineData("-100.01")]
    [InlineData("1000")]
    public async Task AnAdjustmentOutOfRangeIs422WithTheField(string adjustment)
    {
        using var client = await ManagerAsync();

        var response = await client.PostAsJsonAsync(
            ListsUrl,
            new { name = "Mala", adjustmentPercent = decimal.Parse(adjustment, System.Globalization.CultureInfo.InvariantCulture), visibility = "AllActive" },
            Ct);

        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "validation.failed");
        Assert.Contains("AdjustmentPercent", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADuplicateNameIs409WithoutCaring()
    {
        using var client = await ManagerAsync();
        await CreateListAsync(client, "POS", 0m);

        var response = await client.PostAsJsonAsync(ListsUrl, new { name = "pos", adjustmentPercent = 5m, visibility = "AllActive" }, Ct);

        await AssertProblemAsync(response, HttpStatusCode.Conflict, "catalog.price_list.name_taken");
    }

    [Fact]
    public async Task WithoutThePermissionIs403AndAnotherTenantToo()
    {
        var factory = await sharedHost.StartTestAsync(connectionString => new QepApiFactory(connectionString));
        using var noPermission = CreateClient(factory, SubjectId, TenantId, CatalogPermissions.ProductManage);
        using var otherTenant = CreateClient(factory, SubjectId, Guid.CreateVersion7().ToString(), CatalogPermissions.PriceListManage);

        var forbidden = await noPermission.GetAsync(ListsUrl, Ct);
        var crossTenant = await otherTenant.GetAsync(ListsUrl, Ct);

        // 403 y no 500: la política de catalog.price_list.manage está registrada.
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, crossTenant.StatusCode);
    }

    [Fact]
    public async Task UpdateNeedsIfMatchAndBumpsTheVersion()
    {
        using var client = await ManagerAsync();
        var list = await CreateListAsync(client, "POS", 0m);
        var url = $"{ListsUrl}/{list.Id}";
        var body = new { name = "POS centro", adjustmentPercent = -5m, visibility = "AllActive" };

        var missing = await client.PutAsJsonAsync(url, body, Ct);
        var stale = await SendAsync(client, HttpMethod.Put, url, list.Version + 1, body);
        var fresh = await SendAsync(client, HttpMethod.Put, url, list.Version, body);

        await AssertProblemAsync(missing, HttpStatusCode.PreconditionRequired, "precondition.if_match_required");
        await AssertProblemAsync(stale, HttpStatusCode.PreconditionFailed, "concurrency.conflict");
        var updated = (await fresh.Content.ReadFromJsonAsync<PriceListDetailDto>(Ct))!;
        Assert.Equal(("POS centro", -5m, 2L), (updated.Name, updated.AdjustmentPercent, updated.Version));
    }

    [Fact]
    public async Task OverridesAreWrittenByBatchPagedAndBumpTheVersion()
    {
        using var client = await ManagerAsync();
        var (_, variantId) = await CreateProductAsync(client, "Olla exprés", "OLL-1", cop: 100_000m);
        var list = await CreateListAsync(client, "Tienda", 10m);
        var url = $"{ListsUrl}/{list.Id}/overrides";

        var written = await SendAsync(client, HttpMethod.Put, url, list.Version, new
        {
            set = new[] { new { variantId, currency = "cop", amount = 95_000m } },
        });

        Assert.Equal(HttpStatusCode.OK, written.StatusCode);
        var result = (await written.Content.ReadFromJsonAsync<PriceListOverridesWriteDto>(Ct))!;
        Assert.Equal(2L, result.Version);
        var row = Assert.Single(result.Rows);
        Assert.Equal(("OLL-1", "Olla exprés", "COP", (decimal?)100_000m, 95_000m),
            (row.Code, row.Name, row.Currency, row.BasePrice, row.Amount));

        var page = (await client.GetFromJsonAsync<PriceListOverridesPageDto>($"{url}?search=oll", Ct))!;
        Assert.Equal((1, 2L), (page.Total, page.Version));
        Assert.Equal(95_000m, Assert.Single(page.Items).Amount);
        var none = (await client.GetFromJsonAsync<PriceListOverridesPageDto>($"{url}?search=zzz", Ct))!;
        Assert.Equal(0, none.Total);

        var detail = (await client.GetFromJsonAsync<PriceListDetailDto>($"{ListsUrl}/{list.Id}", Ct))!;
        Assert.Equal(1, detail.OverrideCount);

        var removed = await SendAsync(client, HttpMethod.Put, url, result.Version, new
        {
            remove = new[] { new { variantId, currency = "COP" } },
        });
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.Equal(0, (await client.GetFromJsonAsync<PriceListOverridesPageDto>(url, Ct))!.Total);
    }

    [Fact]
    public async Task AnOverrideOfAnUnknownVariantIs422WithItsPosition()
    {
        using var client = await ManagerAsync();
        var list = await CreateListAsync(client, "Tienda", 0m);

        var response = await SendAsync(client, HttpMethod.Put, $"{ListsUrl}/{list.Id}/overrides", list.Version, new
        {
            set = new[] { new { variantId = Guid.CreateVersion7(), currency = "COP", amount = 1000m } },
        });

        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "catalog.price_list.variant_not_found");
        Assert.Contains("Set[0].VariantId", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    // D-4: los tres canales siempre, en orden fijo, con «precio base» como null.
    [Fact]
    public async Task ChannelPricesAlwaysListTheThreeChannels()
    {
        using var client = await ManagerAsync();
        var store = await CreateListAsync(client, "Tienda", 10m);

        var before = (await client.GetFromJsonAsync<ChannelPricesDto>(ChannelsUrl, Ct))!;
        var put = await client.PutAsJsonAsync(
            ChannelsUrl, new { channels = new[] { new { channel = "store", priceListId = (Guid?)store.Id } } }, Ct);
        var after = (await put.Content.ReadFromJsonAsync<ChannelPricesDto>(Ct))!;

        Assert.Equal(["Pos", "Store", "Quotations"], before.Channels.Select(item => item.Channel));
        Assert.All(before.Channels, item => Assert.Null(item.PriceList));
        Assert.Equal(
            [("Pos", (Guid?)null), ("Store", (Guid?)store.Id), ("Quotations", (Guid?)null)],
            after.Channels.Select(item => (item.Channel, item.PriceList?.Id)));
        var detail = (await client.GetFromJsonAsync<PriceListDetailDto>($"{ListsUrl}/{store.Id}", Ct))!;
        Assert.Equal(["Store"], detail.Channels);

        // Volver al precio base y reasignar a otra lista también funcionan.
        var pos = await CreateListAsync(client, "POS", -5m);
        var reassigned = await client.PutAsJsonAsync(
            ChannelsUrl,
            new { channels = new object[] { new { channel = "Store", priceListId = (Guid?)pos.Id }, new { channel = "Pos", priceListId = (Guid?)null } } },
            Ct);
        Assert.Equal(
            [("Pos", (Guid?)null), ("Store", (Guid?)pos.Id), ("Quotations", (Guid?)null)],
            (await reassigned.Content.ReadFromJsonAsync<ChannelPricesDto>(Ct))!.Channels.Select(item => (item.Channel, item.PriceList?.Id)));
    }

    [Fact]
    public async Task AnUnknownChannelOrListIsRejected()
    {
        using var client = await ManagerAsync();

        var badChannel = await client.PutAsJsonAsync(
            ChannelsUrl, new { channels = new[] { new { channel = "Shopify", priceListId = (Guid?)null } } }, Ct);
        var badList = await client.PutAsJsonAsync(
            ChannelsUrl, new { channels = new[] { new { channel = "Pos", priceListId = (Guid?)Guid.CreateVersion7() } } }, Ct);

        await AssertProblemAsync(badChannel, HttpStatusCode.UnprocessableEntity, "validation.failed");
        await AssertProblemAsync(badList, HttpStatusCode.NotFound, "catalog.price_list.not_found");
    }

    [Fact]
    public async Task AListUsedByAChannelCannotBeDeleted()
    {
        using var client = await ManagerAsync();
        var pos = await CreateListAsync(client, "POS", 0m);
        await client.PutAsJsonAsync(ChannelsUrl, new { channels = new[] { new { channel = "Pos", priceListId = (Guid?)pos.Id } } }, Ct);
        var unused = await CreateListAsync(client, "Sin uso", 0m);

        var inUse = await SendAsync(client, HttpMethod.Delete, $"{ListsUrl}/{pos.Id}", pos.Version);
        var deleted = await SendAsync(client, HttpMethod.Delete, $"{ListsUrl}/{unused.Id}", unused.Version);

        await AssertProblemAsync(inUse, HttpStatusCode.Conflict, "catalog.price_list.in_use");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
    }

    // D-2 y D-12 contra la base: precio fijo tal cual; si no, base con el ajuste y la regla del tenant (COP sin decimales); sin
    // lista, el base.
    [Fact]
    public async Task TheChannelPricingAppliesTheListToWhatWasSaved()
    {
        var factory = await sharedHost.StartTestAsync(connectionString => new QepApiFactory(connectionString));
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);
        var (_, fixedVariant) = await CreateProductAsync(client, "Olla", "P-1", cop: 100_000m);
        var (_, adjustedVariant) = await CreateProductAsync(client, "Tetera", "P-2", cop: 10_333m);
        var list = await CreateListAsync(client, "Tienda", 10m);
        await SendAsync(client, HttpMethod.Put, $"{ListsUrl}/{list.Id}/overrides", list.Version, new
        {
            set = new[] { new { variantId = fixedVariant, currency = "COP", amount = 95_000.50m } },
        });
        await client.PutAsJsonAsync(ChannelsUrl, new { channels = new[] { new { channel = "Store", priceListId = (Guid?)list.Id } } }, Ct);

        using var scope = factory.Services.CreateScope();
        var pricing = scope.ServiceProvider.GetRequiredService<IPriceListPricing>();
        var tenant = Guid.Parse(TenantId);
        var variants = new[]
        {
            new PricedVariant(fixedVariant, new Dictionary<string, decimal> { ["COP"] = 100_000m }),
            new PricedVariant(adjustedVariant, new Dictionary<string, decimal> { ["COP"] = 10_333m }),
        };

        var store = await pricing.BookAsync(tenant, await pricing.ListForChannelAsync(tenant, PriceChannel.Store, Ct), variants, Ct);
        var pos = await pricing.BookAsync(tenant, await pricing.ListForChannelAsync(tenant, PriceChannel.Pos, Ct), variants, Ct);

        Assert.Equal(new ListedPrice(95_000.50m, true), store.ListedIn(fixedVariant, "COP"));
        // 10 333 × 1,1 = 11 366,3 → 11 366 con COP sin decimales.
        Assert.Equal(new ListedPrice(11_366m, false), store.ListedIn(adjustedVariant, "COP"));
        Assert.Equal((100_000m, 10_333m), (pos.PriceIn(fixedVariant, "COP"), pos.PriceIn(adjustedVariant, "COP")));
    }

    private async Task<HttpClient> ManagerAsync()
    {
        var factory = await sharedHost.StartTestAsync(connectionString => new QepApiFactory(connectionString));
        return CreateClient(factory, SubjectId, TenantId, ManagePermissions);
    }

    private async Task<PriceListDetailDto> CreateListAsync(HttpClient client, string name, decimal adjustmentPercent)
    {
        var response = await client.PostAsJsonAsync(ListsUrl, new { name, adjustmentPercent, visibility = "AllActive" }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PriceListDetailDto>(Ct))!;
    }

    private async Task<(Guid ProductId, Guid VariantId)> CreateProductAsync(HttpClient client, string name, string code, decimal cop)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products",
            ProductBodies.Create(name, code, pricing: new { prices = new Dictionary<string, decimal> { ["COP"] = cop } }),
            Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var product = (await response.Content.ReadFromJsonAsync<ProductResponse>(Ct))!;
        return (product.Id, product.Only().Id);
    }

    private static Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string url, long version, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return client.SendAsync(request, Ct);
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Contains(code, await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    private static HttpClient CreateClient(QepApiFactory factory, string subjectId, string tenantId, params string[] permissions)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Subject-Id", subjectId);
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenantId);
        client.DefaultRequestHeaders.Add("X-Permissions", string.Join(',', permissions));
        return client;
    }

    private sealed class QepApiFactory(string connectionString) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:QepDatabase", connectionString);
            builder.UseSetting("Seed:Enabled", "false");
            builder.UseSetting("OpenTelemetry:Endpoint", string.Empty);
            builder.UseSetting("Storage:R2:AccountId", "test-account");
            builder.UseSetting("Storage:R2:AccessKeyId", "test-access-key");
            builder.UseSetting("Storage:R2:SecretAccessKey", "test-secret");
            builder.UseSetting("Storage:R2:Bucket", "test-bucket");
            builder.UseSetting("Notifications:EmailProvider", "log");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:DryRun", "true");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:MinimumAgeHours", "24");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:IntervalHours", "24");
            builder.UseSetting("Quotations:PaymentProofs:PublicLinks", "false");
        }
    }
}
