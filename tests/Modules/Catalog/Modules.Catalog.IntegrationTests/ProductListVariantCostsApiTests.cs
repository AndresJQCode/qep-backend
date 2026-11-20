using System.Collections.Concurrent;
using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Modules.Catalog.Application;
using Modules.Catalog.Infrastructure.Persistence;

namespace Modules.Catalog.IntegrationTests;

/// <summary>
/// Spec 2026-11-19 catalogo-lista-costos-de-variantes: <c>GET /catalog/products?includeVariants=true</c> trae el costo de
/// cada variante vendible con la misma semántica que el detalle (null sin permiso, <c>{}</c> sin costo), leído en una sola
/// consulta por página. El catálogo de la orden de compra lo pinta en la tarjeta y prellena el costo de la línea.
/// </summary>
public sealed class ProductListVariantCostsApiTests(SharedCatalogHost sharedHost) : IClassFixture<SharedCatalogHost>
{
    private const string SubjectId = "01900000-0000-7000-8000-0000000000f6";
    private const string ProbeHeader = "X-Cost-Probe";

    private string TenantId { get; } = Guid.CreateVersion7().ToString();

    private static readonly string[] Base = [CatalogPermissions.ProductRead, CatalogPermissions.ProductManage];

    private static readonly string[] CostManager =
        [.. Base, CatalogPermissions.ProductCostRead, CatalogPermissions.ProductCostManage];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ListWithVariantsCarriesCostsPerCurrencyForACostReader()
    {
        var factory = await sharedHost.StartTestAsync(connectionString => new QepApiFactory(connectionString));
        using var manager = Client(factory, CostManager);
        using var reader = Client(factory, [CatalogPermissions.ProductRead, CatalogPermissions.ProductCostRead]);
        var withCost = await CreateAsync(manager, "Vela", "LC-1", new Dictionary<string, decimal>
        {
            // USD primero a propósito: el orden de catálogo es COP, USD.
            ["USD"] = 3m,
            ["COP"] = 12_000m,
        });
        var without = await CreateAsync(manager, "Jabón", "LC-2", null);

        using var json = await ListJsonAsync(reader, "?includeVariants=true");

        var costs = VariantOf(json, withCost).GetProperty("costs");
        Assert.Equal(["COP", "USD"], costs.EnumerateObject().Select(entry => entry.Name));
        Assert.Equal(12_000m, costs.GetProperty("COP").GetDecimal());
        Assert.Equal(3m, costs.GetProperty("USD").GetDecimal());

        var empty = VariantOf(json, without).GetProperty("costs");
        Assert.Equal(JsonValueKind.Object, empty.ValueKind);
        Assert.Empty(empty.EnumerateObject());
    }

    [Fact]
    public async Task ListWithVariantsGivesNullCostsWithoutThePermission()
    {
        var factory = await sharedHost.StartTestAsync(connectionString => new QepApiFactory(connectionString));
        using var manager = Client(factory, CostManager);
        using var reader = Client(factory, [CatalogPermissions.ProductRead]);
        var created = await CreateAsync(manager, "Vela", "LC-1", new Dictionary<string, decimal> { ["COP"] = 12_000m });

        using var json = await ListJsonAsync(reader, "?includeVariants=true");

        Assert.Equal(JsonValueKind.Null, VariantOf(json, created).GetProperty("costs").ValueKind);
    }

    // Una sola consulta a product_variant_costs por request, la página tenga uno o tres productos con varias variantes, y
    // ninguna para quien no puede ver el costo: sin el permiso la tabla ni se toca.
    [Fact]
    public async Task CostsAreReadInOneQueryPerPageAndNotAtAllWithoutThePermission()
    {
        var factory = await sharedHost.StartTestAsync(connectionString => new QepApiFactory(connectionString));
        using var manager = Client(factory, CostManager);
        using var blind = Client(factory, [CatalogPermissions.ProductRead]);
        var cop = new Dictionary<string, decimal> { ["COP"] = 1_000m };
        await CreateAsync(manager, "Vela", "QC-1", cop);
        await CreateAsync(manager, "Jabón", "QC-2", cop);
        await CreateAsync(manager, "Crema", "QC-3", cop);

        await ListJsonAsync(manager, "?includeVariants=true&pageSize=1", probe: "one");
        await ListJsonAsync(manager, "?includeVariants=true&pageSize=3", probe: "three");
        await ListJsonAsync(blind, "?includeVariants=true&pageSize=3", probe: "blind");

        Assert.Single(factory.Counter.CostQueriesOf("one"));
        Assert.Single(factory.Counter.CostQueriesOf("three"));
        Assert.Empty(factory.Counter.CostQueriesOf("blind"));
    }

    private string ProductsUrl => $"/api/v1/tenants/{TenantId}/catalog/products";

    private static JsonElement VariantOf(JsonDocument list, Guid productId) =>
        list.RootElement.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("id").GetGuid() == productId)
            .GetProperty("variants")[0];

    private async Task<Guid> CreateAsync(
        HttpClient client, string name, string code, Dictionary<string, decimal>? costs)
    {
        var response = await client.PostAsJsonAsync(
            ProductsUrl, ProductBodies.Create(name, code, costs: costs), Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return body.RootElement.GetProperty("id").GetGuid();
    }

    private async Task<JsonDocument> ListJsonAsync(HttpClient client, string query, string? probe = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ProductsUrl}{query}");
        if (probe is not null)
        {
            request.Headers.Add(ProbeHeader, probe);
        }

        var response = await client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
    }

    private HttpClient Client(QepApiFactory factory, string[] permissions)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Subject-Id", SubjectId);
        client.DefaultRequestHeaders.Add("X-Tenant-Id", TenantId);
        client.DefaultRequestHeaders.Add("X-Permissions", string.Join(',', permissions));
        return client;
    }

    /// <summary>
    /// Anota, por el header <see cref="ProbeHeader"/> del request en curso, cada comando de <c>CatalogDbContext</c> que lee
    /// <c>product_variant_costs</c>. Sin request o sin header no anota nada, así los workers del host no suman.
    /// </summary>
    internal sealed class CostQueryCounter : DbCommandInterceptor
    {
        private readonly ConcurrentDictionary<string, ConcurrentQueue<string>> _byProbe = new(StringComparer.Ordinal);

        public IHttpContextAccessor? HttpContextAccessor { get; set; }

        public IReadOnlyList<string> CostQueriesOf(string probe) =>
            _byProbe.TryGetValue(probe, out var commands) ? [.. commands] : [];

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Record(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return ValueTask.FromResult(result);
        }

        private void Record(DbCommand command)
        {
            var probe = HttpContextAccessor?.HttpContext?.Request.Headers[ProbeHeader].ToString();
            if (string.IsNullOrEmpty(probe)
                || !command.CommandText.Contains("product_variant_costs", StringComparison.Ordinal))
            {
                return;
            }

            _byProbe.GetOrAdd(probe, _ => new ConcurrentQueue<string>()).Enqueue(command.CommandText);
        }
    }

    internal sealed class QepApiFactory(string connectionString) : WebApplicationFactory<Program>
    {
        public CostQueryCounter Counter { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:QepDatabase", connectionString);
            // Pinned, never inherited: the developer's user-secrets may turn the seed on.
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
            builder.ConfigureTestServices(services =>
                services.ConfigureDbContext<CatalogDbContext>((provider, options) =>
                {
                    Counter.HttpContextAccessor ??= provider.GetRequiredService<IHttpContextAccessor>();
                    options.AddInterceptors(Counter);
                }));
        }
    }
}
