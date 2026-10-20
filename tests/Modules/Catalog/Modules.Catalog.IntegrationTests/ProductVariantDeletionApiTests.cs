using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Modules.Catalog.Application;
using Modules.Catalog.Infrastructure.Persistence;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Modules.Catalog.IntegrationTests;

/// <summary>
/// Variantes, Tarea 6 (spec 2026-10-20 §3.3): una variante guardada que el PUT omite se borra si
/// ninguna línea la vende y se desactiva si alguna sí; <c>canDelete</c> lo anticipa.
///
/// **El uso entra por un doble de <see cref="IVariantUsageChecker"/>, no por líneas reales.** Las
/// columnas <c>variant_id</c> de <c>quotation_items</c>, <c>order_items</c> y <c>sale_lines</c> las
/// crean las Tareas 10 y 12; hasta entonces el adaptador real no puede correr, y armar una
/// cotización, un pedido o una venta del POS a mano desde Catalog es justo el cuerpo crudo que
/// CLAUDE.md pide evitar. El SQL del adaptador tiene su propia prueba
/// (<c>Bootstrapper.UnitTests.CatalogVariantUsageCheckerTests</c>). El doble va por scope, sobre el
/// <see cref="CatalogDbContext"/> de la petición, para poder afirmar en qué llamada había una
/// transacción abierta.
/// </summary>
public sealed class ProductVariantDeletionApiTests
{
    private const string TenantId = "01900000-0000-7000-8000-000000000061";
    private const string SubjectId = "01900000-0000-7000-8000-000000000062";

    private static readonly string[] All = [CatalogPermissions.ProductRead, CatalogPermissions.ProductManage];
    private static readonly string[] Sizes = ["S", "M"];

    // Sin líneas que la vendan, omitirla del PUT la borra: no vuelve en la respuesta ni en el GET y
    // la fila desaparece de catalog.product_variants. La que queda se puede borrar también.
    [Fact]
    public async Task PutUnusedVariantAbsentFromBodyIsDeleted()
    {
        await using var database = await StartDatabaseAsync();
        var usage = new ScriptedUsage();
        using var factory = new QepApiFactory(database.GetConnectionString(), usage);
        using var client = CreateClient(factory);

        var (talla, created) = await CreateTwoSizesAsync(client);
        var small = created.Variants[0];
        var medium = created.Variants[1];

        var response = await PutAsync(client, created.Id, new
        {
            expectedVersion = created.Version,
            name = "Camiseta",
            optionIds = new[] { talla },
            variants = new[] { new { id = small.Id, code = small.Code } }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await ReadProductAsync(response);
        var only = Assert.Single(updated.Variants);
        Assert.Equal(small.Id, only.Id);
        Assert.True(only.CanDelete);
        Assert.DoesNotContain((await GetAsync(client, created.Id)).Variants, variant => variant.Id == medium.Id);
        Assert.Equal(0, await CountVariantRowsAsync(database, medium.Id));
    }

    // Una variante con líneas: el detalle la marca canDelete=false (y la otra true), y omitirla del
    // PUT no la borra: vuelve inactiva, con su código, y la fila sigue en la base.
    [Fact]
    public async Task PutUsedVariantMarkedCanDeleteFalse()
    {
        await using var database = await StartDatabaseAsync();
        var usage = new ScriptedUsage();
        using var factory = new QepApiFactory(database.GetConnectionString(), usage);
        using var client = CreateClient(factory);

        var (talla, created) = await CreateTwoSizesAsync(client);
        var small = created.Variants[0];
        var medium = created.Variants[1];
        usage.Used.Add(medium.Id);

        var fetched = await GetAsync(client, created.Id);
        Assert.True(fetched.Variants.Single(variant => variant.Id == small.Id).CanDelete);
        Assert.False(fetched.Variants.Single(variant => variant.Id == medium.Id).CanDelete);

        var response = await PutAsync(client, created.Id, new
        {
            expectedVersion = fetched.Version,
            name = "Camiseta",
            optionIds = new[] { talla },
            variants = new[] { new { id = small.Id, code = small.Code } }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await ReadProductAsync(response);
        Assert.Equal(2, updated.Variants.Count);
        var kept = updated.Variants.Single(variant => variant.Id == medium.Id);
        Assert.False(kept.IsActive);
        Assert.False(kept.CanDelete);
        Assert.Equal("CAM-M", kept.Code);
        Assert.True(updated.Variants.Single(variant => variant.Id == small.Id).IsActive);
        Assert.Equal(1, await CountVariantRowsAsync(database, medium.Id));
    }

    // Enfoque de revisión 3. La primera comprobación dice «sin uso» y, antes del borrado, aparece
    // una línea: la segunda comprobación —dentro de la transacción del PUT— la ve, y la variante se
    // desactiva en vez de borrarse. Nunca una línea con variant_id huérfano.
    [Fact]
    public async Task PutLineAppearsBetweenCheckAndDeleteDeactivatesInsteadOfDeleting()
    {
        await using var database = await StartDatabaseAsync();
        var usage = new ScriptedUsage();
        using var factory = new QepApiFactory(database.GetConnectionString(), usage);
        using var client = CreateClient(factory);

        var (talla, created) = await CreateTwoSizesAsync(client);
        var small = created.Variants[0];
        var medium = created.Variants[1];
        usage.Calls.Clear();
        // Vacío en la primera llamada del PUT; desde la segunda, la variante ya tiene una línea.
        usage.Script = (call, _) => call == 0 ? new HashSet<Guid>() : new HashSet<Guid> { medium.Id };

        var response = await PutAsync(client, created.Id, new
        {
            expectedVersion = created.Version,
            name = "Camiseta",
            optionIds = new[] { talla },
            variants = new[] { new { id = small.Id, code = small.Code } }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await ReadProductAsync(response);
        var kept = updated.Variants.Single(variant => variant.Id == medium.Id);
        Assert.False(kept.IsActive);
        Assert.False(kept.CanDelete);
        Assert.Equal(1, await CountVariantRowsAsync(database, medium.Id));

        // La primera, fuera de la transacción; la segunda —la que decide el borrado— adentro, y sólo
        // sobre la variante que se iba a borrar.
        Assert.True(usage.Calls.Count >= 2);
        Assert.Equal(new[] { medium.Id }, usage.Calls[0].Ids);
        Assert.False(usage.Calls[0].InTransaction);
        Assert.Equal(new[] { medium.Id }, usage.Calls[1].Ids);
        Assert.True(usage.Calls[1].InTransaction);
    }

    private static async Task<(Guid Talla, ProductResponse Product)> CreateTwoSizesAsync(HttpClient client)
    {
        var option = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{TenantId}/catalog/options",
            new { name = "Talla", values = Sizes },
            TestContext.Current.CancellationToken);
        Assert.True(option.IsSuccessStatusCode);
        var talla = await option.Content.ReadFromJsonAsync<OptionResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(talla);
        Guid ValueOf(string name) => talla.Values.Single(value => value.Name == name).Id;

        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products",
            new
            {
                name = "Camiseta",
                optionIds = new[] { talla.Id },
                variants = new[]
                {
                    new { code = "CAM-S", optionValueIds = new[] { ValueOf("S") }, pricing = ProductBodies.UsdPricing(10m) },
                    new { code = "CAM-M", optionValueIds = new[] { ValueOf("M") }, pricing = ProductBodies.UsdPricing(10m) },
                }
            },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await ReadProductAsync(response);
        // Recién creadas, ninguna línea puede venderlas: el POST no consulta y las dos se pueden borrar.
        Assert.All(created.Variants, variant => Assert.True(variant.CanDelete));
        return (talla.Id, created);
    }

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, Guid productId, object body) =>
        client.PutAsJsonAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products/{productId}", body, TestContext.Current.CancellationToken);

    private static async Task<ProductResponse> GetAsync(HttpClient client, Guid productId) =>
        await ReadProductAsync(await client.GetAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products/{productId}", TestContext.Current.CancellationToken));

    private static async Task<ProductResponse> ReadProductAsync(HttpResponseMessage response)
    {
        Assert.True(
            response.IsSuccessStatusCode,
            $"Se esperaba 2xx y llegó {(int)response.StatusCode}: " +
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var product = await response.Content.ReadFromJsonAsync<ProductResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(product);
        return product;
    }

    private static async Task<long> CountVariantRowsAsync(PostgreSqlContainer database, Guid variantId)
    {
        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM catalog.product_variants WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", variantId);
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private static async Task<PostgreSqlContainer> StartDatabaseAsync()
    {
        var database = new PostgreSqlBuilder("postgres:18-alpine")
            .WithDatabase("qep")
            .WithUsername("qep")
            .WithPassword("qep-integration")
            .Build();
        await database.StartAsync(TestContext.Current.CancellationToken);
        return database;
    }

    private static HttpClient CreateClient(QepApiFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Subject-Id", SubjectId);
        client.DefaultRequestHeaders.Add("X-Tenant-Id", TenantId);
        client.DefaultRequestHeaders.Add("X-Permissions", string.Join(',', All));
        return client;
    }

    /// <summary>
    /// Lo que la prueba controla: qué variantes están «en uso» y, si hace falta, una respuesta por
    /// número de llamada. Anota cada llamada con sus ids y si había una transacción abierta.
    /// </summary>
    private sealed class ScriptedUsage
    {
        private readonly Lock _gate = new();

        public HashSet<Guid> Used { get; } = [];

        public Func<int, IReadOnlyCollection<Guid>, IReadOnlySet<Guid>>? Script { get; set; }

        public List<(Guid[] Ids, bool InTransaction)> Calls { get; } = [];

        public HashSet<Guid> Answer(IReadOnlyCollection<Guid> ids, bool inTransaction)
        {
            lock (_gate)
            {
                var call = Calls.Count;
                Calls.Add((ids.ToArray(), inTransaction));
                var used = Script?.Invoke(call, ids) ?? Used;
                return ids.Where(used.Contains).ToHashSet();
            }
        }
    }

    private sealed class ScriptedUsageChecker(ScriptedUsage usage, CatalogDbContext catalog) : IVariantUsageChecker
    {
        public Task<IReadOnlySet<Guid>> FindUsedAsync(
            Guid tenantId, IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlySet<Guid>>(usage.Answer(variantIds, catalog.Database.CurrentTransaction is not null));
    }

    private sealed class QepApiFactory(string connectionString, ScriptedUsage usage) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:QepDatabase", connectionString);
            builder.UseSetting("OpenTelemetry:Endpoint", string.Empty);
            builder.UseSetting("Storage:R2:AccountId", "test-account");
            builder.UseSetting("Storage:R2:AccessKeyId", "test-access-key");
            builder.UseSetting("Storage:R2:SecretAccessKey", "test-secret");
            builder.UseSetting("Storage:R2:Bucket", "test-bucket");
            builder.UseSetting("Storage:R2:PublicBucket", "test-public-bucket");
            builder.UseSetting("Storage:R2:PublicBaseUrl", "https://cdn.qep.test");
            // Fijado, nunca heredado de appsettings.json. SDD-CT-17.
            builder.UseSetting("Notifications:EmailProvider", "log");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:DryRun", "true");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:MinimumAgeHours", "24");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:IntervalHours", "24");
            builder.UseSetting("Quotations:PaymentProofs:PublicLinks", "false");
            builder.ConfigureTestServices(services => services.AddScoped<IVariantUsageChecker>(provider =>
                new ScriptedUsageChecker(usage, provider.GetRequiredService<CatalogDbContext>())));
        }
    }
}
