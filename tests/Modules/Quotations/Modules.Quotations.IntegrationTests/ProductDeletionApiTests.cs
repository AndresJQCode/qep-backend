using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Quotations.Application;
using Npgsql;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// El <c>DELETE</c> de producto visto desde cotizaciones (spec 2026-11-08), por HTTP y contra el esquema migrado: la
/// línea de una cotización enviada se queda con código y nombre congelados, el borrador pierde la línea y se
/// revaloriza, y todo eso va en la misma transacción que el borrado del producto.
/// </summary>
public sealed class ProductDeletionApiTests
{
    private static string ProductUrl(Guid tenantId, Guid productId) =>
        $"/api/v1/tenants/{tenantId}/catalog/products/{productId}";

    private static async Task<HttpResponseMessage> DeleteProductAsync(HttpClient client, Guid tenantId, Guid productId, long version)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, ProductUrl(tenantId, productId));
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<(long Version, string Code, string Name)> ReadProductAsync(
        HttpClient client, Guid tenantId, Guid productId)
    {
        var product = await client.GetFromJsonAsync<JsonElement>(
            ProductUrl(tenantId, productId), TestContext.Current.CancellationToken);
        var variant = product.GetProperty("variants")[0];
        return (product.GetProperty("version").GetInt64(), variant.GetProperty("code").GetString()!,
            variant.GetProperty("name").GetString()!);
    }

    private static async Task<string?> ReadCodeAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return body.RootElement.GetProperty("code").GetString();
    }

    private static async Task ExecuteAsync(string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<long> CountAsync(string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private static async Task<(string? Code, string? Name)> SentSnapshotAsync(string connectionString, Guid quotationId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT product_code, product_name FROM quotations.quotation_items WHERE quotation_id = @id", connection);
        command.Parameters.AddWithValue("id", quotationId);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        return (reader.IsDBNull(0) ? null : reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    private static async Task<QuotationResponse> GetQuotationAsync(HttpClient client, Guid tenantId, Guid quotationId)
    {
        var body = await client.GetFromJsonAsync<QuotationResponse>(
            $"{QuotationsUrl(tenantId)}/{quotationId}", TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        return body;
    }

    private sealed record World(
        HttpClient Client, Guid TenantId, string ConnectionString, Guid Deleted, Guid Kept, QuotationResponse Sent,
        QuotationResponse Draft) : IDisposable
    {
        public void Dispose() => Client.Dispose();
    }

    /// <summary>
    /// Un producto que se va a borrar y otro que se queda; una cotización enviada con el primero, con la foto de la
    /// línea anulada como la tienen las enviadas antes de la Tarea 6, y un borrador con los dos.
    /// </summary>
    private static async Task<World> ArrangeAsync(QepApiFactory factory, string connectionString)
    {
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        var customerId = await CreateActiveCustomerAsync(client, tenantId);
        var deleted = await CreateProductWithScalesAsync(client, tenantId);
        var kept = await CreateProductWithScalesAsync(client, tenantId);
        var sent = await CreateSentQuotationAsync(client, factory, tenantId, customerId, deleted);
        var draft = await CreateQuotationAsync(client, tenantId, customerId);
        foreach (var variantId in new[] { deleted, kept })
        {
            var added = await client.PostAsJsonAsync(
                $"{QuotationsUrl(tenantId)}/{draft.Id}/items",
                new AddQuotationItemRequest(variantId, 1m),
                TestContext.Current.CancellationToken);
            Assert.True(added.IsSuccessStatusCode, await added.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        await ExecuteAsync(
            connectionString,
            "UPDATE quotations.quotation_items SET product_code = NULL, product_name = NULL WHERE quotation_id = @id",
            ("id", sent.Id));
        return new World(client, tenantId, connectionString, deleted, kept, sent, await GetQuotationAsync(client, tenantId, draft.Id));
    }

    // Lo que una prueba que falla a propósito tiene que ver intacto.
    private static async Task AssertUntouchedAsync(World world)
    {
        Assert.Equal((null, null), await SentSnapshotAsync(world.ConnectionString, world.Sent.Id));
        var draft = await GetQuotationAsync(world.Client, world.TenantId, world.Draft.Id);
        Assert.Equal(2, draft.Items.Count);
        Assert.Contains(draft.Items, item => item.VariantId == world.Deleted);
        Assert.Equal(world.Draft.Version, draft.Version);
        var product = await world.Client.GetAsync(ProductUrl(world.TenantId, world.Deleted), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, product.StatusCode);
    }

    [Fact]
    public async Task DeletingAProductFreezesOldSentLinesAndRepricesDrafts()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var world = await ArrangeAsync(factory, database.GetConnectionString());
        var (version, code, name) = await ReadProductAsync(world.Client, world.TenantId, world.Deleted);

        var response = await DeleteProductAsync(world.Client, world.TenantId, world.Deleted, version);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal((code, name), await SentSnapshotAsync(world.ConnectionString, world.Sent.Id));
        var draft = await GetQuotationAsync(world.Client, world.TenantId, world.Draft.Id);
        Assert.Equal(world.Kept, Assert.Single(draft.Items).VariantId);
        Assert.Equal(world.Draft.Version + 1, draft.Version);
    }

    [Fact]
    public async Task AStaleVersionTouchesNothing()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var world = await ArrangeAsync(factory, database.GetConnectionString());
        var (version, _, _) = await ReadProductAsync(world.Client, world.TenantId, world.Deleted);

        var response = await DeleteProductAsync(world.Client, world.TenantId, world.Deleted, version + 1);

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
        Assert.Equal("concurrency.conflict", await ReadCodeAsync(response));
        await AssertUntouchedAsync(world);
    }

    // Una falla después de la limpieza —el DELETE del producto mismo— deshace también lo que la limpieza escribió en
    // Quotations y el aviso de las fotos: todo es una transacción.
    [Fact]
    public async Task AFailureAfterTheCleanupLeavesEverythingAsItWas()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var world = await ArrangeAsync(factory, database.GetConnectionString());
        // Una foto en la galería, para que el aviso de liberación tenga algo que escribir si nada se revirtiera.
        await ExecuteAsync(
            world.ConnectionString,
            "INSERT INTO catalog.product_gallery_images (id, product_id, file_id, position) VALUES (gen_random_uuid(), @id, gen_random_uuid(), 0)",
            ("id", world.Deleted));
        var (version, _, _) = await ReadProductAsync(world.Client, world.TenantId, world.Deleted);
        await ExecuteAsync(
            world.ConnectionString,
            $"""
            CREATE FUNCTION public.fail_product_delete() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF OLD.id = '{world.Deleted}' THEN
                    RAISE EXCEPTION 'borrado del producto fallido a propósito';
                END IF;
                RETURN OLD;
            END $$;
            CREATE TRIGGER fail_product_delete BEFORE DELETE ON catalog.products
                FOR EACH ROW EXECUTE FUNCTION public.fail_product_delete();
            """);

        try
        {
            var response = await DeleteProductAsync(world.Client, world.TenantId, world.Deleted, version);

            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            await AssertUntouchedAsync(world);
            Assert.Equal(0L, await CountAsync(
                world.ConnectionString,
                "SELECT count(*) FROM platform.outbox_messages WHERE event_name = 'catalog.product.images-released.v1'"));
        }
        finally
        {
            await ExecuteAsync(
                world.ConnectionString,
                "DROP TRIGGER fail_product_delete ON catalog.products; DROP FUNCTION public.fail_product_delete();");
        }
    }

    // Review Focus 3: el borrador abierto en otra pestaña lleva la versión de antes del DELETE, y guardarlo devolvería la
    // línea borrada. 412, y la línea sigue fuera.
    [Fact]
    public async Task ADraftOpenInAnotherTabGets412()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var world = await ArrangeAsync(factory, database.GetConnectionString());
        var (version, _, _) = await ReadProductAsync(world.Client, world.TenantId, world.Deleted);
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await DeleteProductAsync(world.Client, world.TenantId, world.Deleted, version)).StatusCode);

        using var save = new HttpRequestMessage(HttpMethod.Put, $"{QuotationsUrl(world.TenantId)}/{world.Draft.Id}")
        {
            Content = JsonContent.Create(new SaveQuotationRequest(
                world.Draft.ValidUntil,
                world.Draft.PaymentMethod,
                "Guardado desde la otra pestaña",
                null,
                null,
                [new QuotationEditItemRequest(world.Deleted, 1m), new QuotationEditItemRequest(world.Kept, 1m)])),
        };
        save.Headers.TryAddWithoutValidation("If-Match", $"\"{world.Draft.Version}\"");
        var response = await world.Client.SendAsync(save, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
        Assert.Equal("concurrency.conflict", await ReadCodeAsync(response));
        var draft = await GetQuotationAsync(world.Client, world.TenantId, world.Draft.Id);
        Assert.Equal(world.Kept, Assert.Single(draft.Items).VariantId);
    }

    // Ruling del controlador: un choque dentro de la limpieza es 412 concurrency.conflict, nunca 500. Un trigger simula
    // cada camino: el guardado del borrador que otro cambió (0 filas → concurrencia del unit of work de Quotations), un
    // deadlock en el congelado de las líneas y una falla de serialización en el SQL de la tienda.
    [Theory]
    [InlineData("quotations.quotations", "lost_update")]
    [InlineData("quotations.quotation_items", "40P01")]
    [InlineData("store.order_request_lines", "40001")]
    public async Task AConflictInsideTheCleanupIs412AndTouchesNothing(string table, string failure)
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var world = await ArrangeAsync(factory, database.GetConnectionString());
        var (version, _, _) = await ReadProductAsync(world.Client, world.TenantId, world.Deleted);
        // lost_update: la fila del borrador «desaparece» para el UPDATE, como si otra escritura la hubiera cambiado.
        var trigger = failure == "lost_update"
            ? $"""
              CREATE FUNCTION public.race_product_delete() RETURNS trigger LANGUAGE plpgsql AS $$
              BEGIN
                  RETURN NULL;
              END $$;
              CREATE TRIGGER race_product_delete BEFORE UPDATE ON {table}
                  FOR EACH ROW EXECUTE FUNCTION public.race_product_delete();
              """
            : $"""
              CREATE FUNCTION public.race_product_delete() RETURNS trigger LANGUAGE plpgsql AS $$
              BEGIN
                  RAISE EXCEPTION 'carrera simulada' USING ERRCODE = '{failure}';
              END $$;
              CREATE TRIGGER race_product_delete BEFORE UPDATE OR DELETE ON {table}
                  FOR EACH STATEMENT EXECUTE FUNCTION public.race_product_delete();
              """;
        await ExecuteAsync(world.ConnectionString, trigger);

        try
        {
            var response = await DeleteProductAsync(world.Client, world.TenantId, world.Deleted, version);

            Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
            Assert.Equal("concurrency.conflict", await ReadCodeAsync(response));
            await AssertUntouchedAsync(world);
        }
        finally
        {
            await ExecuteAsync(
                world.ConnectionString,
                $"DROP TRIGGER race_product_delete ON {table}; DROP FUNCTION public.race_product_delete();");
        }
    }
}
