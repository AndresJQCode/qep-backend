using System.Net;
using System.Net.Http.Json;
using BuildingBlocks.Application;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Catalog.Application;
using Modules.Catalog.Domain;
using Modules.Catalog.Infrastructure.Persistence;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Modules.Catalog.IntegrationTests;

/// <summary>
/// Variantes: la biblioteca de opciones por tenant (spec 2026-10-20 §3.1). Siembra
/// «Talla» y «Color» inactivas en el primer acceso, una sola vez; una opción o un valor que
/// alguna variante usa no se borra (409 <c>catalog.option.in_use</c>), uno sin uso sí.
/// </summary>
public sealed class OptionLibraryApiTests
{
    private const string TenantId = "01900000-0000-7000-8000-00000000f001";
    private const string SubjectId = "01900000-0000-7000-8000-00000000f002";
    private const string OtherTenantId = "01900000-0000-7000-8000-00000000f0dd";
    private const string OtherSubjectId = "01900000-0000-7000-8000-00000000f0dc";
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    private static readonly string[] Manage =
        [CatalogPermissions.ProductRead, CatalogPermissions.ProductManage];

    private static readonly string[] ReadOnly = [CatalogPermissions.ProductRead];

    [Fact]
    public async Task FirstListSeedsTallaAndColorInactive()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, Manage);

        var options = await ListAsync(client, TenantId);

        Assert.Equal(["Color", "Talla"], options.Items.Select(option => option.Name));
        Assert.All(options.Items, option => Assert.False(option.IsActive));
        Assert.All(options.Items, option => Assert.True(option.CanDelete));
        var talla = options.Items.Single(option => option.Name == "Talla");
        Assert.Equal(["XS", "S", "M", "L", "XL", "XXL"], talla.Values.Select(value => value.Name));
        Assert.All(talla.Values, value => Assert.True(value.IsActive && value.CanDelete));
        var color = options.Items.Single(option => option.Name == "Color");
        Assert.Equal(
            ["Negro", "Blanco", "Gris", "Azul", "Rojo", "Verde", "Amarillo", "Café", "Beige", "Rosado"],
            color.Values.Select(value => value.Name));
    }

    [Fact]
    public async Task SecondListDoesNotDuplicateSeed()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, Manage);

        await ListAsync(client, TenantId);
        var second = await ListAsync(client, TenantId);

        Assert.Equal(2, second.Items.Count);
        Assert.Equal(2, await CountAsync(database, "SELECT count(*) FROM catalog.option_definitions"));
        Assert.Equal(16, await CountAsync(database, "SELECT count(*) FROM catalog.option_values"));
    }

    // Sin la marca de aprovisionamiento, el ON CONFLICT de la semilla no ve nada con qué chocar
    // y la predefinida borrada volvería en el siguiente listado.
    [Fact]
    public async Task ADeletedSeedDoesNotComeBack()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, Manage);

        var talla = (await ListAsync(client, TenantId)).Items.Single(option => option.Name == "Talla");
        Assert.Equal(HttpStatusCode.NoContent, (await DeleteAsync(client, TenantId, talla.Id)).StatusCode);

        var after = await ListAsync(client, TenantId);

        Assert.Equal(["Color"], after.Items.Select(option => option.Name));
    }

    [Fact]
    public async Task CreateReturnsAnActiveOptionWithItsValuesInOrder()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, Manage);

        var response = await CreateAsync(client, TenantId, " Material ", ["Algodón", " Lino "]);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await ReadAsync<OptionResponse>(response);
        Assert.Equal("Material", created.Name);
        Assert.True(created.IsActive);
        Assert.Equal(["Algodón", "Lino"], created.Values.Select(value => value.Name));
    }

    [Fact]
    public async Task CreateRejectsDuplicateNameInTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, Manage);
        using var other = CreateClient(factory, OtherSubjectId, OtherTenantId, Manage);
        await ListAsync(client, TenantId);

        var response = await CreateAsync(client, TenantId, " talla ", ["S"]);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("catalog.option.name_taken", await BodyAsync(response), StringComparison.Ordinal);
        // El nombre es único por tenant, no global.
        Assert.Equal(HttpStatusCode.Created, (await CreateAsync(other, OtherTenantId, "Talla", ["S"])).StatusCode);
    }

    [Fact]
    public async Task CreateWithBlankValueReturnsTheErrorsMap()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, Manage);

        var response = await CreateAsync(client, TenantId, "Material", ["Algodón", " "]);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await BodyAsync(response);
        Assert.Contains("validation.failed", body, StringComparison.Ordinal);
        Assert.Contains("\"errors\"", body, StringComparison.Ordinal);
        Assert.Contains("Values[1]", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateReplacesNameStateAndValuesInOrder()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, Manage);
        var talla = (await ListAsync(client, TenantId)).Items.Single(option => option.Name == "Talla");
        var small = talla.Values.Single(value => value.Name == "S");
        var medium = talla.Values.Single(value => value.Name == "M");

        var response = await UpdateAsync(client, TenantId, talla.Id, new
        {
            name = "Talla adulto",
            isActive = true,
            values = new object[]
            {
                new { id = medium.Id, name = "Mediana", isActive = true },
                new { id = small.Id, name = "S", isActive = false },
                new { id = (Guid?)null, name = "3XL", isActive = true },
            },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await ReadAsync<OptionResponse>(response);
        Assert.Equal("Talla adulto", updated.Name);
        Assert.True(updated.IsActive);
        Assert.Equal(["Mediana", "S", "3XL"], updated.Values.Select(value => value.Name));
        Assert.Equal(medium.Id, updated.Values[0].Id);
        Assert.False(updated.Values[1].IsActive);
        var reloaded = (await ListAsync(client, TenantId)).Items.Single(option => option.Id == talla.Id);
        Assert.Equal(["Mediana", "S", "3XL"], reloaded.Values.Select(value => value.Name));
    }

    // isActive es obligatorio en la opción y en cada valor: un cuerpo que lo omite no desactiva en
    // silencio, responde 422 con el campo marcado y deja la opción como estaba.
    [Fact]
    public async Task UpdateWithoutIsActiveIsRejectedWithTheErrorsMapAndChangesNothing()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, Manage);
        var created = await ReadAsync<OptionResponse>(await CreateAsync(client, TenantId, "Material", ["Algodón"]));
        var cotton = created.Values.Single();

        var response = await UpdateAsync(client, TenantId, created.Id, new
        {
            name = "Material",
            values = new object[] { new { id = cotton.Id, name = cotton.Name } },
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await BodyAsync(response);
        Assert.Contains("validation.failed", body, StringComparison.Ordinal);
        Assert.Contains("\"IsActive\"", body, StringComparison.Ordinal);
        Assert.Contains("\"Values[0].IsActive\"", body, StringComparison.Ordinal);
        var reloaded = (await ListAsync(client, TenantId)).Items.Single(option => option.Id == created.Id);
        Assert.True(reloaded.IsActive);
        Assert.True(Assert.Single(reloaded.Values).IsActive);
    }

    [Fact]
    public async Task DeleteUnusedOptionSucceeds()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, Manage);
        var created = await ReadAsync<OptionResponse>(await CreateAsync(client, TenantId, "Material", ["Algodón"]));

        var response = await DeleteAsync(client, TenantId, created.Id);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0, await CountAsync(database, $"SELECT count(*) FROM catalog.option_definitions WHERE id = '{created.Id}'"));
        Assert.Equal(0, await CountAsync(database, $"SELECT count(*) FROM catalog.option_values WHERE option_definition_id = '{created.Id}'"));
    }

    [Fact]
    public async Task DeleteOptionUsedByVariantReturnsInUse()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, Manage);
        var created = await ReadAsync<OptionResponse>(await CreateAsync(client, TenantId, "Material", ["Algodón", "Lino"]));
        await SaveProductUsingAsync(factory, created.Id, created.Values[0].Id);

        var response = await DeleteAsync(client, TenantId, created.Id);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("catalog.option.in_use", await BodyAsync(response), StringComparison.Ordinal);
        Assert.Equal(1, await CountAsync(database, $"SELECT count(*) FROM catalog.option_definitions WHERE id = '{created.Id}'"));

        // La pantalla sabe sin adivinar qué puede borrar: la opción y el valor usado no; el otro sí.
        var listed = (await ListAsync(client, TenantId)).Items.Single(option => option.Id == created.Id);
        Assert.False(listed.CanDelete);
        Assert.False(listed.Values[0].CanDelete);
        Assert.True(listed.Values[1].CanDelete);
    }

    [Fact]
    public async Task UpdateThatDropsAValueInUseReturnsInUseAndAnUnusedOneIsDeleted()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, Manage);
        var created = await ReadAsync<OptionResponse>(await CreateAsync(client, TenantId, "Material", ["Algodón", "Lino"]));
        var used = created.Values[0];
        var unused = created.Values[1];
        await SaveProductUsingAsync(factory, created.Id, used.Id);

        var dropUsed = await UpdateAsync(client, TenantId, created.Id, new
        {
            name = "Material",
            isActive = true,
            values = new[] { new { id = unused.Id, name = unused.Name, isActive = true } },
        });
        Assert.Equal(HttpStatusCode.Conflict, dropUsed.StatusCode);
        Assert.Contains("catalog.option.in_use", await BodyAsync(dropUsed), StringComparison.Ordinal);

        var dropUnused = await UpdateAsync(client, TenantId, created.Id, new
        {
            name = "Material",
            isActive = true,
            values = new[] { new { id = used.Id, name = used.Name, isActive = false } },
        });
        Assert.Equal(HttpStatusCode.OK, dropUnused.StatusCode);
        Assert.Equal(0, await CountAsync(database, $"SELECT count(*) FROM catalog.option_values WHERE id = '{unused.Id}'"));
    }

    // La carrera: el valor empieza a usarse entre la consulta del handler y el commit. La FK
    // RESTRICT lo frena y la unidad de trabajo lo traduce al mismo 409, no a un 500.
    [Fact]
    public async Task ForeignKeyViolationOnDeleteSaysInUse()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, Manage);
        var created = await ReadAsync<OptionResponse>(await CreateAsync(client, TenantId, "Material", ["Algodón"]));

        await using var scope = factory.Services.CreateAsyncScope();
        var option = await scope.ServiceProvider.GetRequiredService<CatalogDbContext>().OptionDefinitions
            .Include(value => value.Values)
            .SingleAsync(value => value.Id == new OptionDefinitionId(created.Id), TestContext.Current.CancellationToken);
        await SaveProductUsingAsync(factory, created.Id, created.Values[0].Id);
        scope.ServiceProvider.GetRequiredService<CatalogDbContext>().OptionDefinitions.Remove(option);

        var error = await Assert.ThrowsAsync<ResourceConflictException>(() =>
            scope.ServiceProvider.GetRequiredService<ICatalogUnitOfWork>()
                .SaveChangesAsync(TestContext.Current.CancellationToken));

        Assert.Equal("catalog.option.in_use", error.Code);
    }

    [Fact]
    public async Task UnknownOptionIsNotFound()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, Manage);

        var response = await DeleteAsync(client, TenantId, Guid.CreateVersion7());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("catalog.option.not_found", await BodyAsync(response), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OtherTenantGets403()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var owner = CreateClient(factory, SubjectId, TenantId, Manage);
        using var intruder = CreateClient(factory, OtherSubjectId, OtherTenantId, Manage);
        var created = await ReadAsync<OptionResponse>(await CreateAsync(owner, TenantId, "Material", ["Algodón"]));

        var list = await intruder.GetAsync(OptionsPath(TenantId), TestContext.Current.CancellationToken);
        var create = await CreateAsync(intruder, TenantId, "Intruso", []);
        var update = await UpdateAsync(intruder, TenantId, created.Id, new { name = "X", isActive = true, values = Array.Empty<object>() });
        var delete = await DeleteAsync(intruder, TenantId, created.Id);

        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, update.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
        Assert.Equal(1, await CountAsync(database, $"SELECT count(*) FROM catalog.option_definitions WHERE id = '{created.Id}'"));
    }

    [Fact]
    public async Task ReadWithoutManagePermissionCanList()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var reader = CreateClient(factory, SubjectId, TenantId, ReadOnly);

        var list = await reader.GetAsync(OptionsPath(TenantId), TestContext.Current.CancellationToken);
        var create = await CreateAsync(reader, TenantId, "Material", ["Algodón"]);

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
    }

    private static string OptionsPath(string tenantId) => $"/api/v1/tenants/{tenantId}/catalog/options";

    private static async Task<OptionsResponse> ListAsync(HttpClient client, string tenantId)
    {
        var response = await client.GetAsync(OptionsPath(tenantId), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<OptionsResponse>(response);
    }

    private static Task<HttpResponseMessage> CreateAsync(
        HttpClient client, string tenantId, string name, string[] values) =>
        client.PostAsJsonAsync(OptionsPath(tenantId), new { name, values }, TestContext.Current.CancellationToken);

    private static Task<HttpResponseMessage> UpdateAsync(
        HttpClient client, string tenantId, Guid optionId, object body) =>
        client.PutAsJsonAsync($"{OptionsPath(tenantId)}/{optionId}", body, TestContext.Current.CancellationToken);

    private static Task<HttpResponseMessage> DeleteAsync(HttpClient client, string tenantId, Guid optionId) =>
        client.DeleteAsync($"{OptionsPath(tenantId)}/{optionId}", TestContext.Current.CancellationToken);

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<T>(TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        return body;
    }

    private static Task<string> BodyAsync(HttpResponseMessage response) =>
        response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

    // Las variantes todavía no entran por HTTP (Tarea 5): el producto con opción se guarda por el
    // agregado, como en CatalogVariantPersistenceTests.
    private static async Task SaveProductUsingAsync(QepApiFactory factory, Guid optionId, Guid valueId)
    {
        var product = Product.Create(
            ProductId.New(),
            Guid.Parse(TenantId),
            "Camiseta básica",
            ProductDetails.Empty,
            Variant("CAM-001", [], id: null),
            [],
            Now);
        product.Update(
            "Camiseta básica",
            ProductDetails.Empty,
            [optionId],
            [Variant("CAM-001", [valueId], id: product.Variants.Single().Id.Value)],
            [],
            Now);

        await using var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IProductRepository>().Add(product);
        await scope.ServiceProvider.GetRequiredService<ICatalogUnitOfWork>()
            .SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static ProductVariantInput Variant(string code, IReadOnlyList<Guid> values, Guid? id) =>
        new()
        {
            Id = id,
            Code = code,
            OptionValueIds = values,
            Pricing = new ProductPricing
            {
                Prices = new Dictionary<string, decimal> { ["USD"] = 25m },
                Scales = [],
                PackagingUnits = [],
            },
        };

    private static async Task<int> CountAsync(PostgreSqlContainer database, string sql)
    {
        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(TestContext.Current.CancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
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

    private static HttpClient CreateClient(
        QepApiFactory factory, string subjectId, string tenantId, string[] permissions)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Subject-Id", subjectId);
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenantId);
        client.DefaultRequestHeaders.Add("X-Permissions", string.Join(',', permissions));
        return client;
    }

    private sealed class QepApiFactory(string connectionString)
        : WebApplicationFactory<Program>
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
            // Fijado, nunca heredado de appsettings.json. SDD-CT-17.
            builder.UseSetting("Notifications:EmailProvider", "log");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:DryRun", "true");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:MinimumAgeHours", "24");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:IntervalHours", "24");
            builder.UseSetting("Quotations:PaymentProofs:PublicLinks", "false");
        }
    }
}
