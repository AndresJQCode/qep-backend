using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Modules.Storage.IntegrationTests;

/// <summary>
/// CAT-09 — la galería de archivos de una entidad.
///
/// Hasta este slice, <c>OwnerId</c> y <c>OwnerType</c> se escribían al crear el archivo y se
/// devolvían en la respuesta, pero **no se podía consultar por ellos**: el dato entraba y no
/// salía. Eso es lo que el spec de <c>CAT-05</c> dio por resuelto y no lo estaba.
///
/// El criterio que manda es CA-CAT-09-06: el filtro por dueño acota **dentro** del tenant y
/// nunca en su lugar. Un <c>OwnerId</c> es único de por sí, así que una implementación que
/// filtrara sólo por él pasaría todas las demás pruebas de este archivo y publicaría la
/// biblioteca ajena.
/// </summary>
public sealed class FileOwnerFilterApiTests
{
    private const string TenantA = "01900000-0000-7000-8000-0000000e0001";
    private const string SubjectA = "01900000-0000-7000-8000-0000000e0002";
    private const string TenantB = "01900000-0000-7000-8000-0000000f0001";
    private const string SubjectB = "01900000-0000-7000-8000-0000000f0002";
    private const string Permissions = "storage.file.upload,storage.file.read";

    // CA-CAT-09-01
    [Fact]
    public async Task ListingByOwnerReturnsOnlyThatOwnersFiles()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectA, TenantA);
        var product = Guid.CreateVersion7();
        var otherProduct = Guid.CreateVersion7();

        var first = await CreateFileAsync(client, TenantA, product, "Product", "foto-1.png");
        var second = await CreateFileAsync(client, TenantA, product, "Product", "foto-2.png");
        var alien = await CreateFileAsync(client, TenantA, otherProduct, "Product", "otra.png");
        await MakeAvailableAsync(database, first, second, alien);

        var page = await ListAsync(client, TenantA, $"?ownerId={product}&ownerType=Product");

        Assert.Equal(2, page.TotalCount);
        Assert.Equal(2, page.Items.Count);
        Assert.DoesNotContain(page.Items, item => item.Id == alien);
    }

    // CA-CAT-09-02: un ownerType inexistente FALLA. No se ignora el filtro ni se devuelve la
    // lista completa, que es lo que hace hoy el filtro de status.
    [Fact]
    public async Task AnUnknownOwnerTypeIsRejectedInsteadOfIgnored()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectA, TenantA);
        var product = Guid.CreateVersion7();
        var file = await CreateFileAsync(client, TenantA, product, "Product", "foto.png");
        await MakeAvailableAsync(database, file);

        var response = await client.GetAsync(
            $"/api/v1/tenants/{TenantA}/files?ownerId={product}&ownerType=Producto",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        Assert.Contains("storage.file.owner_type_invalid", body, StringComparison.Ordinal);
    }

    // CA-CAT-09-03: medio filtro no es medio resultado.
    [Theory]
    [InlineData("?ownerId={0}")]
    [InlineData("?ownerType=Product")]
    public async Task HalfAnOwnerFilterIsRejected(string queryTemplate)
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectA, TenantA);
        var query = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            queryTemplate,
            Guid.CreateVersion7());

        var response = await client.GetAsync(
            $"/api/v1/tenants/{TenantA}/files{query}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        Assert.Contains("storage.file.owner_filter_incomplete", body, StringComparison.Ordinal);
    }

    // CA-CAT-09-04: el filtro es opcional y no cambia el contrato que ya existía.
    [Fact]
    public async Task ListingWithoutTheOwnerFilterBehavesAsBefore()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectA, TenantA);

        var first = await CreateFileAsync(client, TenantA, Guid.CreateVersion7(), "Product", "a.png");
        var second = await CreateFileAsync(client, TenantA, Guid.CreateVersion7(), "User", "b.png");
        await MakeAvailableAsync(database, first, second);

        var page = await ListAsync(client, TenantA, query: string.Empty);

        Assert.Equal(2, page.TotalCount);
    }

    // CA-CAT-09-05: la galería hereda la exclusión de PendingUpload del listado. Las subidas a
    // medio camino no son fotos del producto todavía.
    [Fact]
    public async Task PendingUploadsDoNotShowUpInTheGallery()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectA, TenantA);
        var product = Guid.CreateVersion7();

        var available = await CreateFileAsync(client, TenantA, product, "Product", "lista.png");
        await CreateFileAsync(client, TenantA, product, "Product", "a-medias.png");
        await MakeAvailableAsync(database, available);

        var page = await ListAsync(client, TenantA, $"?ownerId={product}&ownerType=Product");

        Assert.Equal(1, page.TotalCount);
        Assert.Equal(available, Assert.Single(page.Items).Id);
    }

    // CA-CAT-09-06 — el criterio que justifica la revisión de riesgo. El filtro por dueño acota
    // dentro del tenant, no lo reemplaza.
    [Fact]
    public async Task TheOwnerFilterNeverReachesAcrossTenants()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var owner = CreateClient(factory, SubjectB, TenantB);
        using var intruder = CreateClient(factory, SubjectA, TenantA);
        var product = Guid.CreateVersion7();

        var foreign = await CreateFileAsync(owner, TenantB, product, "Product", "ajena.png");
        await MakeAvailableAsync(database, foreign);

        // El mismo ownerId, desde el otro tenant. Si el filtro se aplicara en lugar del de
        // tenant y no además, esta lista traería el archivo ajeno.
        var page = await ListAsync(intruder, TenantA, $"?ownerId={product}&ownerType=Product");

        Assert.Equal(0, page.TotalCount);
        Assert.Empty(page.Items);
    }

    // CA-CAT-09-07: los filtros se combinan, no se reemplazan.
    [Fact]
    public async Task TheOwnerFilterCombinesWithTheOtherFilters()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectA, TenantA);
        var product = Guid.CreateVersion7();

        var image = await CreateFileAsync(client, TenantA, product, "Product", "foto.png");
        var pdf = await CreateFileAsync(
            client, TenantA, product, "Product", "ficha.pdf", "application/pdf");
        await MakeAvailableAsync(database, image, pdf);

        var page = await ListAsync(client, TenantA, $"?ownerId={product}&ownerType=Product&kind=image");

        Assert.Equal(1, page.TotalCount);
        Assert.Equal(image, Assert.Single(page.Items).Id);
    }

    // CA-CAT-09-08: el índice se verifica en el catálogo de PostgreSQL y no en el DbContext.
    // Declararlo en el modelo y olvidar la migración compila y pasa todas las demás pruebas.
    [Fact]
    public async Task TheOwnerIndexExistsInTheDatabase()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectA, TenantA);
        // Una llamada cualquiera fuerza a que las migraciones se apliquen antes de mirar.
        await ListAsync(client, TenantA, query: string.Empty);

        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT indexdef
            FROM pg_indexes
            WHERE schemaname = 'storage'
              AND tablename = 'file_resources'
              AND indexname = 'IX_file_resources_tenant_owner'
            """,
            connection);
        var definition = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(definition);
        var text = (string)definition;
        Assert.Contains("tenant_id", text, StringComparison.Ordinal);
        Assert.Contains("owner_type", text, StringComparison.Ordinal);
        Assert.Contains("owner_id", text, StringComparison.Ordinal);
    }

    private static async Task<Guid> CreateFileAsync(
        HttpClient client,
        string tenantId,
        Guid ownerId,
        string ownerType,
        string name,
        string mimeType = "image/png")
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/files",
            new { ownerId, ownerType, name, mimeType, sizeBytes = 1024 },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<UploadSessionPayload>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(payload);
        return payload.FileResourceId;
    }

    // El estado se fuerza por SQL con el NOMBRE del valor, no con su número: la columna es
    // varchar y PostgreSQL acepta '3' por cast de asignación, con lo que Enum.Parse lo
    // materializa como Available y la prueba pasa por accidente. Es la trampa que CAT-05
    // detectó a tiempo y dejó documentada.
    private static async Task MakeAvailableAsync(
        PostgreSqlContainer database,
        params Guid[] fileIds)
    {
        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "UPDATE storage.file_resources SET status = 'Available' WHERE id = ANY(@ids)",
            connection);
        command.Parameters.AddWithValue("ids", fileIds);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<PagedFilesPayload> ListAsync(
        HttpClient client,
        string tenantId,
        string query)
    {
        var response = await client.GetAsync(
            $"/api/v1/tenants/{tenantId}/files{query}",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<PagedFilesPayload>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(page);
        return page;
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
        QepApiFactory factory,
        string subjectId,
        string tenantId)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Subject-Id", subjectId);
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenantId);
        client.DefaultRequestHeaders.Add("X-Permissions", Permissions);
        return client;
    }

    private sealed record UploadSessionPayload(Guid FileResourceId, string UploadUrl, string StorageKey);

    private sealed record FileItemPayload(Guid Id, string Status, Guid OwnerId, string OwnerType);

    private sealed record PagedFilesPayload(IReadOnlyList<FileItemPayload> Items, int TotalCount);

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
        }
    }
}
