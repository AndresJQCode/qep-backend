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
        // Ajuste 2026-10-05: visible por decisión del owner, al final y con su nombre por defecto.
        "Total facturado",
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
        // Spec 2026-10-05, D8: la hoja se llama como la busca el importador del ERP, sin tilde.
        Assert.Equal("MIGRACION 1", layout.SheetName);
        Assert.Equal(
            SeededLayoutHeaders,
            layout.Columns.Where(column => column.Visible).Select(column => column.Header));
        Assert.Equal(21, layout.Columns.Count(column => column.Kind == OrdersExportColumnKind.Fixed));
        // Ajuste 2026-09-26: "Nit" es el NIT de la empresa por la que se factura, no uno escrito a
        // mano: una cotización facturada por otra empresa lo llevaba mal.
        Assert.Equal(
            "company_tax_id",
            Assert.Single(layout.Columns, column => column.Header == "Nit").Key);
        // Ajuste 2026-09-26: "IVA" es la tasa de cada línea (tax_rate), no un 0.19 fijo: hay
        // productos con otra tarifa. "tax" (el monto) sigue oculta, con su nombre por defecto.
        Assert.Equal(
            "tax_rate",
            Assert.Single(layout.Columns, column => column.Visible && column.Header == "IVA").Key);
        var tax = Assert.Single(layout.Columns, column => column.Key == "tax");
        Assert.False(tax.Visible);
        Assert.Equal("IVA", tax.Header);
        // Ajuste 2026-10-03: el ERP lee en "Valor Unit" el precio por unidad con el descuento ya
        // aplicado y sin IVA, que es "unit_price". "unit_price_without_tax" (sin IVA, pero antes del
        // descuento) queda oculta, con su nombre por defecto.
        Assert.Equal(
            "unit_price",
            Assert.Single(layout.Columns, column => column.Visible && column.Header == "Valor Unit").Key);
        var unitPriceWithoutTax = Assert.Single(layout.Columns, column => column.Key == "unit_price_without_tax");
        Assert.False(unitPriceWithoutTax.Visible);
        Assert.Equal("Valor Unit sin IVA", unitPriceWithoutTax.Header);
        // Ajuste 2026-09-26: el ERP lee en las dos formas de pago el banco con el número de cuenta.
        // "bank" queda oculta, con su nombre por defecto. Ajuste 2026-10-03: cada forma de pago con
        // su comprobante —"payment_method_N"—, no "bank_account", que es del pedido y llenaba la 2
        // con una sola consignación; "bank_account" queda oculta, con su nombre por defecto.
        Assert.Equal(
            "payment_method_1",
            Assert.Single(layout.Columns, column => column.Header == "Forma de pago 1").Key);
        Assert.Equal(
            "payment_method_2",
            Assert.Single(layout.Columns, column => column.Header == "Forma de pago 2").Key);
        var bankAccount = Assert.Single(layout.Columns, column => column.Key == "bank_account");
        Assert.False(bankAccount.Visible);
        Assert.Equal("Banco y cuenta", bankAccount.Header);
        var bank = Assert.Single(layout.Columns, column => column.Key == "bank");
        Assert.False(bank.Visible);
        Assert.Equal("Banco", bank.Header);
        // Ajuste 2026-09-26: "V. Consignacion (P7)" es el total de todos los comprobantes; el primero
        // sigue en "V. Consignacion 1".
        Assert.Equal(
            "proof_amount_total",
            Assert.Single(layout.Columns, column => column.Header == "V. Consignacion (P7)").Key);
        Assert.Equal(
            "V. Consignacion 1",
            Assert.Single(layout.Columns, column => column.Key == "proof_amount_1").Header);
        Assert.Equal(
            ["FECHA", "Bloq/act", "Vencimiento"],
            layout.Columns.Where(column => column.Key == "order_date").Select(column => column.Header));
        Assert.Equal(
            "Nota Encab.",
            Assert.Single(layout.Columns, column => column.Key == "customer_name").Header);
        // Ajuste 2026-09-26: el ERP importa en "Documento (P5)" el documento de identidad, no el
        // CUC. "document" sigue en la lista, oculta y con su nombre por defecto.
        Assert.Equal(
            "customer_identification",
            Assert.Single(layout.Columns, column => column.Header == "Documento (P5)").Key);
        var document = Assert.Single(layout.Columns, column => column.Key == "document");
        Assert.False(document.Visible);
        Assert.Equal("Documento", document.Header);
        // Ajuste 2026-10-02: la hoja ya fija "Transportadora (P2)" = Coordinadora, así que
        // "Ciudad (P4)" es la ciudad como la escribe Coordinadora. "city" (el nombre del DANE) queda
        // en la lista, oculta y con su nombre por defecto.
        Assert.Equal(
            "coordinadora_city",
            Assert.Single(layout.Columns, column => column.Header == "Ciudad (P4)").Key);
        var city = Assert.Single(layout.Columns, column => column.Key == "city");
        Assert.False(city.Visible);
        Assert.Equal("Ciudad", city.Header);
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
        // D8: la semilla sólo crea. El tenant de producción ya tiene layout, y su nombre de hoja se
        // cambia a mano desde la pantalla.
        Assert.Equal("Pedidos", stored.SheetName);
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
