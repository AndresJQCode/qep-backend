using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Modules.Catalog.Infrastructure.Persistence;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Modules.Catalog.IntegrationTests;

/// <summary>
/// Spec 2026-10-20 §8: cada producto de antes pasa a ser un producto sin opciones con una variante
/// por defecto que lleva **el mismo id** que el producto, su código, precios, escalas y empaques;
/// la imagen vieja pasa a ser la primera foto de la galería y el historial de precios queda en esa
/// variante. Mismo mecanismo que <see cref="ProductPricesMigrationTests"/>: migrar hasta la anterior
/// con IMigrator, sembrar como está producción y aplicar AddProductVariants.
/// </summary>
public sealed class ProductVariantsMigrationTests
{
    private const string LastMigrationBeforeVariants = "20261009025831_AddProductPrices";

    private const string TenantId = "01900000-0000-7000-8000-00000000d001";
    private const string ProductId = "01900000-0000-7000-8000-00000000d002";
    private const string NoImageProductId = "01900000-0000-7000-8000-00000000d003";
    private const string ImageFileId = "01900000-0000-7000-8000-00000000d004";
    private const string ScaleSixId = "01900000-0000-7000-8000-00000000d005";
    private const string ScaleFiftyId = "01900000-0000-7000-8000-00000000d006";
    private const string ChangeId = "01900000-0000-7000-8000-00000000d007";
    private const string AuthorId = "01900000-0000-7000-8000-00000000d008";

    private const string LegacySql = $"""
        INSERT INTO catalog.products (
            id, tenant_id, name, code, is_active, version, created_at, updated_at,
            image_file_id, packaging_units)
        VALUES
            ('{ProductId}', '{TenantId}', 'Bronceador', 'BR-7416', true, 4,
             '2026-09-01T12:00:00Z', '2026-09-02T12:00:00Z', '{ImageFileId}', ARRAY[100,150]),
            ('{NoImageProductId}', '{TenantId}', 'Keratina', 'KE-7701', false, 1,
             '2026-09-01T12:00:00Z', '2026-09-01T12:00:00Z', NULL, ARRAY[]::integer[]);
        INSERT INTO catalog.product_prices (product_id, currency, amount)
        VALUES ('{ProductId}', 'COP', 35900), ('{ProductId}', 'USD', 9.97),
               ('{NoImageProductId}', 'COP', 114700.50);
        INSERT INTO catalog.product_price_scales (
            id, product_id, tenant_id, from_unit, to_unit, discount, restriction, multiple,
            allow_grouping)
        VALUES ('{ScaleSixId}', '{ProductId}', '{TenantId}', 6, 48, 15, 'Multiple', 6, true),
               ('{ScaleFiftyId}', '{ProductId}', '{TenantId}', 50, 500, 20, 'PackagingUnit', NULL, false);
        INSERT INTO catalog.product_price_changes (
            id, tenant_id, product_id, field, currency, scale_from_unit, scale_to_unit,
            previous_value, new_value, changed_by, changed_at)
        VALUES ('{ChangeId}', '{TenantId}', '{ProductId}', 'PriceBase', 'COP', NULL, NULL, 34000, 35900,
                '{AuthorId}', '2026-09-02T12:00:00Z');
        """;

    [Fact]
    public async Task AddProductVariantsTurnsEveryProductIntoOneDefaultVariantWithItsId()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await using var context = NewCatalogContext(connectionString);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(LastMigrationBeforeVariants, TestContext.Current.CancellationToken);
        await ExecuteAsync(connectionString, LegacySql);

        await migrator.MigrateAsync(MigrationId(context, "_AddProductVariants"), TestContext.Current.CancellationToken);

        // Mismo id, mismo código, empaques intactos, activa como su producto y sin código de barras.
        Assert.Equal(
            new[]
            {
                $"{ProductId}|{ProductId}|{TenantId}|BR-7416|{{100,150}}|0|true|",
                $"{NoImageProductId}|{NoImageProductId}|{TenantId}|KE-7701|{{}}|0|true|",
            },
            await RowsAsync(
                connectionString,
                "SELECT id::text || '|' || product_id::text || '|' || tenant_id::text || '|' || code || '|' || " +
                "packaging_units::text || '|' || position::text || '|' || is_active::text || '|' || " +
                "coalesce(barcode, '') FROM catalog.product_variants ORDER BY id"));
        Assert.Equal(
            new[]
            {
                $"{ProductId}|COP|35900.00",
                $"{ProductId}|USD|9.97",
                $"{NoImageProductId}|COP|114700.50",
            },
            await RowsAsync(
                connectionString,
                "SELECT variant_id::text || '|' || currency || '|' || amount::text " +
                "FROM catalog.product_prices ORDER BY variant_id, currency"));
        Assert.Equal(
            new[]
            {
                $"{ScaleSixId}|{ProductId}|6|48|15.00|Multiple|6|true",
                $"{ScaleFiftyId}|{ProductId}|50|500|20.00|PackagingUnit||false",
            },
            await RowsAsync(
                connectionString,
                "SELECT id::text || '|' || variant_id::text || '|' || from_unit || '|' || to_unit || '|' || " +
                "discount::text || '|' || restriction || '|' || coalesce(multiple::text, '') || '|' || " +
                "allow_grouping::text FROM catalog.product_price_scales ORDER BY from_unit"));

        // La imagen vieja es la primera foto de la galería, y la variante la muestra.
        var gallery = await RowsAsync(
            connectionString,
            "SELECT id::text || '|' || product_id::text || '|' || file_id::text || '|' || position::text " +
            "FROM catalog.product_gallery_images ORDER BY product_id");
        var photo = Assert.Single(gallery);
        var photoId = photo.Split('|')[0];
        Assert.Equal($"{photoId}|{ProductId}|{ImageFileId}|0", photo);
        Assert.Equal(photoId, await ScalarAsync<string>(
            connectionString, $"SELECT gallery_image_id::text FROM catalog.product_variants WHERE id = '{ProductId}'"));
        Assert.True(await ScalarAsync<bool>(
            connectionString,
            $"SELECT gallery_image_id IS NULL FROM catalog.product_variants WHERE id = '{NoImageProductId}'"));

        // El historial queda en la variante por defecto, sin perder el producto.
        Assert.Equal(
            $"{ProductId}|{ProductId}",
            await ScalarAsync<string>(
                connectionString,
                "SELECT product_id::text || '|' || variant_id::text FROM catalog.product_price_changes " +
                $"WHERE id = '{ChangeId}'"));

        // El código, la imagen y los empaques ya no viven en el producto.
        Assert.Equal(0L, await ScalarAsync<long>(
            connectionString,
            "SELECT count(*) FROM information_schema.columns WHERE table_schema = 'catalog' " +
            "AND table_name = 'products' AND column_name IN ('code', 'image_file_id', 'packaging_units')"));

        // Deja la base en la última versión: el modelo de hoy pide todo lo que vino después.
        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RevertingPutsCodeImageAndPackagingBackOnTheProduct()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await using var context = NewCatalogContext(connectionString);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(LastMigrationBeforeVariants, TestContext.Current.CancellationToken);
        await ExecuteAsync(connectionString, LegacySql);
        await migrator.MigrateAsync(MigrationId(context, "_AddProductVariants"), TestContext.Current.CancellationToken);

        await migrator.MigrateAsync(LastMigrationBeforeVariants, TestContext.Current.CancellationToken);

        Assert.Equal(
            new[]
            {
                $"{ProductId}|BR-7416|{ImageFileId}|{{100,150}}",
                $"{NoImageProductId}|KE-7701||{{}}",
            },
            await RowsAsync(
                connectionString,
                "SELECT id::text || '|' || code || '|' || coalesce(image_file_id::text, '') || '|' || " +
                "packaging_units::text FROM catalog.products ORDER BY id"));
        Assert.Equal(
            new[] { $"{ProductId}|COP|35900.00", $"{ProductId}|USD|9.97", $"{NoImageProductId}|COP|114700.50" },
            await RowsAsync(
                connectionString,
                "SELECT product_id::text || '|' || currency || '|' || amount::text " +
                "FROM catalog.product_prices ORDER BY product_id, currency"));
        Assert.Equal(2L, await ScalarAsync<long>(
            connectionString, $"SELECT count(*) FROM catalog.product_price_scales WHERE product_id = '{ProductId}'"));

        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
    }

    private static CatalogDbContext NewCatalogContext(string connectionString) =>
        new(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", "catalog"))
            .Options);

    private static string MigrationId(CatalogDbContext context, string suffix) =>
        context.Database.GetMigrations().Single(id => id.EndsWith(suffix, StringComparison.Ordinal));

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private static async Task<string[]> RowsAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var rows = new List<string>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add(reader.GetString(0));
        }

        return [.. rows];
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
}
