using Bootstrapper.Seeding;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Quotations.Infrastructure.Persistence;
using Modules.Tenancy.Infrastructure.Seed;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// La mitad de Quotations de la semilla de arranque: el tenant sembrado numera sus pedidos con la
/// serie del cliente, <c>PW234235</c> — el caso canónico del README (§ Numeración de documentos por
/// tenant). Se prueba contra la fila y no contra un pedido convertido: que la fila produzca ese número
/// ya lo cubren <c>DocumentNumberingApiTests</c>.
/// </summary>
public sealed class QuotationsSeedTests
{
    private const string OwnerEmail = "semilla@qcode.co";

    [Fact]
    public async Task SeedConfiguresTheOrderNumberingOfTheSeedTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var baseFactory = new QepApiFactory(database.GetConnectionString());
        using var factory = WithSeed(baseFactory);
        using var client = factory.CreateClient();

        var formats = await FormatsOfTheSeedTenantAsync(factory);

        var order = Assert.Single(formats);
        Assert.Equal("order", order.DocumentType);
        Assert.Equal("PW", order.Prefix);
        Assert.False(order.IncludeYear);
        Assert.Equal(string.Empty, order.YearSeparator);
        Assert.Equal(1, order.MinDigits);
    }

    // La app reinicia sola en k8s, asi que la semilla corre muchas veces sobre la misma base. Se
    // llama al orquestador de nuevo en vez de levantar otra factory: es lo que hace un reinicio de pod.
    [Fact]
    public async Task SeedingTwiceKeepsASingleOrderFormat()
    {
        await using var database = await StartDatabaseAsync();
        using var baseFactory = new QepApiFactory(database.GetConnectionString());
        using var factory = WithSeed(baseFactory);
        using var client = factory.CreateClient();

        await factory.Services.RunQepSeedAsync(TestContext.Current.CancellationToken);

        var order = Assert.Single(await FormatsOfTheSeedTenantAsync(factory));
        Assert.Equal("PW", order.Prefix);
    }

    // La fila se puede haber cambiado a mano con el runbook del README. El seeder solo crea: si ya hay
    // formato de pedidos para el tenant, lo deja como esta, aunque no sea el de la semilla.
    [Fact]
    public async Task SeedDoesNotOverwriteAnOrderFormatSetByHand()
    {
        await using var database = await StartDatabaseAsync();
        using var baseFactory = new QepApiFactory(database.GetConnectionString());
        using (baseFactory.CreateClient())
        {
            await DocumentNumberingFormatLookupTests.SetDocumentNumberFormatAsync(
                baseFactory, TenancySeeder.SeedTenantId, "order", "OB-", includeYear: true, "/", 6);
        }

        using var factory = WithSeed(baseFactory);
        using var client = factory.CreateClient();

        var order = Assert.Single(await FormatsOfTheSeedTenantAsync(factory));
        Assert.Equal("OB-", order.Prefix);
        Assert.True(order.IncludeYear);
        Assert.Equal("/", order.YearSeparator);
        Assert.Equal(6, order.MinDigits);
    }

    /// <summary>Los encabezados visibles de la hoja de importación del ERP del tenant sembrado
    /// (MIGRACION 1), en su orden. También los lee <c>OrderExportApiTests</c>, que arma el Excel
    /// con este layout de punta a punta.</summary>
    internal static readonly string[] SeededLayoutHeaders =
    [
        "EMPRESA", "DOC.", "PREFIJO", "No. Doc.", "FECHA", "Tercero Externo", "Nota Encab.",
        "Tercero Interno", "Doc. Externo", "Bloq/act", "Forma de pago 1", "V. Consignacion 1",
        "Forma de pago 2", "V. Consignacion 2", "Verificado", "Anulado", "Cod. Producto", "Bodega",
        "U.Medida", "Cantidad", "Valor Unit", "IVA", "Descuento", "Lote", "Centro costos",
        "Nota Detalle", "Vencimiento", "Factor Conversion Cantidad", "Factor conversion",
        "Fecha Pago (P1)", "Transportadora (P2)", "Flete (P3)", "Ciudad (P4)", "Tipo Envio",
        "Documento (P5)", "Pedido (P6)", "V. Consignacion (P7)", "Direccion (P8)", "Observaciones",
        "Guia P9", "Telefono (P10)", "valor flete", "# Rotulos", "Email", "GeneraGuia",
        "GeneraFactura", "Nit",
    ];

    // Ajuste 2026-09-25: el tenant sembrado exporta pedidos con la hoja de su ERP. La fila nace por
    // Replace, así que queda en versión 2 como un primer PUT.
    [Fact]
    public async Task SeedConfiguresTheOrdersExportLayoutOfTheSeedTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var baseFactory = new QepApiFactory(database.GetConnectionString());
        using var factory = WithSeed(baseFactory);
        using var client = factory.CreateClient();

        var layout = Assert.Single(await LayoutsOfTheSeedTenantAsync(factory));

        Assert.Equal(2, layout.Version);
        Assert.Equal(
            SeededLayoutHeaders,
            layout.Columns.Where(column => column.Visible).Select(column => column.Header));
        Assert.Equal(24, layout.Columns.Count(column => column.Kind == OrdersExportColumnKind.Fixed));
        Assert.Equal(
            ["FECHA", "Bloq/act", "Vencimiento"],
            layout.Columns.Where(column => column.Key == "order_date").Select(column => column.Header));
        Assert.Equal(
            "Nota Encab.",
            Assert.Single(layout.Columns, column => column.Key == "customer_name").Header);
        // Todo el catálogo está en la lista, visible o no: si una llave faltara, Effective la
        // completaría visible al final y la hoja tendría una columna que el ERP no espera.
        Assert.Empty(OrdersExportColumnCatalog.Columns
            .Select(column => column.Key)
            .Except(layout.Columns.Where(column => column.Key is not null).Select(column => column.Key!)));
        Assert.Equal(
            SeededLayoutHeaders,
            OrdersExportLayout.Effective(layout).Where(column => column.Visible).Select(column => column.Header));
    }

    [Fact]
    public async Task SeedingTwiceKeepsASingleOrdersExportLayout()
    {
        await using var database = await StartDatabaseAsync();
        using var baseFactory = new QepApiFactory(database.GetConnectionString());
        using var factory = WithSeed(baseFactory);
        using var client = factory.CreateClient();

        await factory.Services.RunQepSeedAsync(TestContext.Current.CancellationToken);

        var layout = Assert.Single(await LayoutsOfTheSeedTenantAsync(factory));
        Assert.Equal(2, layout.Version);
    }

    // Mismo criterio que el formato de pedidos: el tenant pudo haber cambiado sus columnas desde la
    // pantalla, y un reinicio de pod no puede pisarlas.
    [Fact]
    public async Task SeedDoesNotOverwriteAnOrdersExportLayoutSetBeforehand()
    {
        await using var database = await StartDatabaseAsync();
        using var baseFactory = new QepApiFactory(database.GetConnectionString());
        using (baseFactory.CreateClient())
        {
            await using var scope = baseFactory.Services.CreateAsyncScope();
            var layout = OrdersExportLayout.CreateDefault(TenancySeeder.SeedTenantId, DateTimeOffset.UtcNow);
            Assert.True(layout.Replace(
                [OrdersExportColumnSetting.Catalog("email", "Correo", visible: true)],
                DateTimeOffset.UtcNow));
            scope.ServiceProvider.GetRequiredService<IOrdersExportLayoutRepository>().Add(layout);
            await scope.ServiceProvider.GetRequiredService<IQuotationsUnitOfWork>()
                .SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var factory = WithSeed(baseFactory);
        using var client = factory.CreateClient();

        var stored = Assert.Single(await LayoutsOfTheSeedTenantAsync(factory));
        Assert.Equal(2, stored.Version);
        Assert.Equal("Correo", stored.Columns[0].Header);
        Assert.DoesNotContain(stored.Columns, column => column.Kind == OrdersExportColumnKind.Fixed);
    }

    private static async Task<List<OrdersExportLayout>> LayoutsOfTheSeedTenantAsync(
        WebApplicationFactory<Program> factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<QuotationsDbContext>();
        return await dbContext.OrdersExportLayouts
            .AsNoTracking()
            .Where(layout => layout.TenantId == TenancySeeder.SeedTenantId)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    private static WebApplicationFactory<Program> WithSeed(QepApiFactory factory) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Seed:Enabled", "true");
            builder.UseSetting("Seed:OwnerEmail", OwnerEmail);
        });

    private static async Task<List<DocumentNumberingFormat>> FormatsOfTheSeedTenantAsync(
        WebApplicationFactory<Program> factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<QuotationsDbContext>();
        return await dbContext.DocumentNumberingFormats
            .AsNoTracking()
            .Where(format => format.TenantId == TenancySeeder.SeedTenantId)
            .ToListAsync(TestContext.Current.CancellationToken);
    }
}
