using BuildingBlocks.Application.RichText;
using BuildingBlocks.Infrastructure.RichText;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Quotations.Infrastructure.Persistence;
using Npgsql;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// <c>AddPdfSections</c> (spec 2026-11-16 §5.1) siembra a Origen Botánico con los dos textos que hoy trae <c>quotation.typ</c>, para
/// que su PDF no cambie al desplegar, y a nadie más. Migra sólo Quotations sobre una base vacía, como
/// <see cref="QuotationPdfNumberFormatMigrationTests"/>: el host de pruebas migra todo al arrancar y no habría esquema anterior.
/// </summary>
public sealed class PdfContentMigrationTests
{
    private const string OrigenBotanicoId = "01900000-0000-7000-8000-00000000f001";
    private const string OtherTenantId = "01900000-0000-7000-8000-00000000f002";

    internal const string PaymentNoticeTitle = "Ten en cuenta:";

    internal const string PaymentNoticeHtml =
        "<p>no aceptamos pagos por corresponsal bancario,<br>ni depósitos en entidades bancarias, ni cajeros.<br>Solo debes pagar el valor de tu pedido, el valor a pagar<br>debe de ser libre de toda comisión bancaria.</p>";

    internal const string DeliveryProcessTitle = "Proceso de Alistamiento y Recepción de Pedidos.";

    internal const string DeliveryProcessHtml =
        "<p><strong>Alistamiento y Entrega:</strong><br>El proveedor preparará los pedidos bajo filmación y los entregará a la empresa transportadora dentro de los 8 días hábiles siguientes a la confirmación del pedido, siempre que estos hayan sido solicitados y pagados por el mayorista.</p><p><strong>Recepción de la Mercancía:</strong><br>Al recibir los productos de la transportadora, el mayorista deberá revisar el estado del paquete. No recibir en caso de detectar:</p><ul><li>Cinta contramarcada deteriorada</li><li>Cajas dañadas y/o abiertas</li><li>Cintas, zunchos o cajas diferentes a las del proveedor</li></ul><p>Si decide recibir el paquete a pesar de alguna de estas irregularidades, lo hará bajo su responsabilidad. En este caso, deberá:</p><ul><li>Dejar constancia de la novedad en la guía de recepción de la transportadora.</li><li>Informar de inmediato al proveedor.</li><li>Enviar fotografías como evidencia.</li></ul><p><strong>Verificación del Contenido:</strong><br>Una vez recibido el pedido, el mayorista deberá grabar un video al abrir las cajas o paquetes, evidenciando el estado y la cantidad de los productos.</p><p><strong>Reporte de Novedades:</strong><br>Si detecta daños, faltantes o deterioro en los productos, deberá informar al proveedor en un plazo máximo de 1 día hábil después de recibirlos. El reporte debe incluir:</p><ul><li>Video de la apertura del paquete.</li><li>Fotografías de las cajas.</li><li>Imágenes de los 3 stickers identificativos del proveedor.</li></ul>";

    // La base sólo tiene Quotations: la migración lee de tenancy.tenants sólo el id y el slug, así que alcanza con una réplica
    // mínima hecha a mano (como las de CatalogVariantUsageCheckerTests). time_zone porque la prueba sigue hasta la última
    // migración y AddOrderDate (spec 2026-11-16 §6) lo lee de la misma tabla.
    private const string TenantsSql = $"""
        CREATE SCHEMA IF NOT EXISTS tenancy;
        CREATE TABLE tenancy.tenants (id uuid PRIMARY KEY, slug text NOT NULL, time_zone text NULL);
        INSERT INTO tenancy.tenants (id, slug) VALUES
            ('{OrigenBotanicoId}', 'origen-botanico'),
            ('{OtherTenantId}', 'otro-tenant');
        """;

    [Fact]
    public async Task OrigenBotanicoKeepsItsTwoSections()
    {
        await using var database = await MigrateWithTenantsAsync();

        var rows = await RowsAsync(database.GetConnectionString(), OrigenBotanicoId);

        Assert.Equal(
            [
                ("DeliveryProcess", true, true, true, DeliveryProcessTitle, DeliveryProcessHtml),
                ("PaymentNotice", true, true, true, PaymentNoticeTitle, PaymentNoticeHtml),
            ],
            rows);
        Assert.DoesNotContain(rows, row => row.Section == "Terms");
    }

    [Fact]
    public async Task OtherTenantsGetNoRows()
    {
        await using var database = await MigrateWithTenantsAsync();

        Assert.Empty(await RowsAsync(database.GetConnectionString(), OtherTenantId));
    }

    // Si el texto sembrado no fuera un punto fijo del saneador, el primer guardado desde la consola lo cambiaría sin que nadie lo
    // tocara, y el PDF de Origen Botánico se movería.
    [Theory]
    [InlineData(PaymentNoticeHtml)]
    [InlineData(DeliveryProcessHtml)]
    public void TheOrigenBotanicoSeedIsAFixedPointOfTheSanitizer(string html) =>
        Assert.Equal(html, new RichTextHtmlSanitizer().Sanitize(html, "x").Html);

    // Lo sembrado, pasado por el lector real, tiene la forma de los textos fijos de antes en `quotation.typ`: el aviso es un
    // párrafo con sus tres saltos, y el proceso de entrega son cuatro párrafos con rótulo en negrita, uno sin rótulo y tres listas.
    [Fact]
    public void TheOrigenBotanicoSeedPrintsTheFormerTexts()
    {
        var content = PdfContent.Empty with
        {
            PaymentNotice = new PdfSection(PdfSectionKey.PaymentNotice, true, true, true, PaymentNoticeTitle, PaymentNoticeHtml),
            DeliveryProcess = new PdfSection(
                PdfSectionKey.DeliveryProcess, true, true, true, DeliveryProcessTitle, DeliveryProcessHtml),
        };

        var sections = QuotationPdfSectionsBuilder.Build(content, QuotationWithBillingAccount(), new RichTextHtmlReader());

        var paymentNotice = Assert.IsType<QuotationPdfSection>(sections.PaymentNotice);
        Assert.Equal("Ten en cuenta:", paymentNotice.Title);
        var notice = Assert.Single(paymentNotice.Blocks);
        Assert.Equal(RichTextBlockKind.Paragraph, notice.Kind);
        Assert.Equal(3, notice.Runs!.Count(run => run.Break));

        var delivery = Assert.IsType<QuotationPdfSection>(sections.DeliveryProcess);
        Assert.Equal("Proceso de Alistamiento y Recepción de Pedidos.", delivery.Title);
        Assert.Collection(
            delivery.Blocks,
            block => AssertLabeledParagraph(block, "Alistamiento y Entrega:"),
            block => AssertLabeledParagraph(block, "Recepción de la Mercancía:"),
            block => AssertBulletList(block, 3),
            AssertPlainParagraph,
            block => AssertBulletList(block, 3),
            block => AssertLabeledParagraph(block, "Verificación del Contenido:"),
            block => AssertLabeledParagraph(block, "Reporte de Novedades:"),
            block => AssertBulletList(block, 3));
    }

    private static void AssertLabeledParagraph(RichTextBlock block, string label)
    {
        Assert.Equal(RichTextBlockKind.Paragraph, block.Kind);
        var runs = block.Runs!;
        Assert.Equal(new RichTextRun(label, Bold: true), runs[0]);
        Assert.True(runs[1].Break);
    }

    private static void AssertPlainParagraph(RichTextBlock block)
    {
        Assert.Equal(RichTextBlockKind.Paragraph, block.Kind);
        Assert.DoesNotContain(block.Runs!, run => run.Bold);
    }

    private static void AssertBulletList(RichTextBlock block, int items)
    {
        Assert.Equal(RichTextBlockKind.List, block.Kind);
        Assert.False(block.Ordered);
        Assert.Equal(items, block.Items!.Count);
    }

    private static QuotationPdfDocument QuotationWithBillingAccount() => new(
        QuotationNumber: "QUO-2026-0042",
        IssuedOn: new DateOnly(2026, 11, 16),
        ValidUntil: new DateOnly(2026, 11, 30),
        CustomerName: "María Pérez",
        CustomerCuc: "CUC-0042",
        CustomerContact: string.Empty,
        CustomerLocation: string.Empty,
        Billing: new QuotationPdfParty(true, string.Empty, string.Empty, string.Empty),
        Shipping: new QuotationPdfParty(true, string.Empty, string.Empty, string.Empty),
        IsStorePickup: false,
        AdvisorLabel: "Laura Gómez",
        Currency: "COP",
        Symbol: "$",
        Decimals: 0,
        BillingAccount: new QuotationPdfBillingAccount(
            "Comercial Andina S.A.S.", "900123456-7", "NIT", string.Empty, string.Empty, "Bancolombia", "123", "COP"),
        PaymentMethod: null,
        Items: [],
        Subtotal: 1_050_420m,
        DiscountAmount: 0m,
        TaxPercentage: 19m,
        TaxAmount: 199_580m,
        Total: 1_250_000m,
        RetentionAmount: 0m,
        Withholdings: [],
        NetTotal: 1_250_000m,
        CustomerVatSurplus: false,
        Logo: null);

    private static async Task<TestDatabase> MigrateWithTenantsAsync()
    {
        var database = await StartEmptyDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await using var context = NewContext(connectionString);
        var migrator = context.GetService<IMigrator>();
        var migrations = context.Database.GetMigrations().ToList();
        var target = migrations.Single(id => id.EndsWith("_AddPdfSections", StringComparison.Ordinal));
        var previous = migrations[migrations.IndexOf(target) - 1];

        await migrator.MigrateAsync(previous, TestContext.Current.CancellationToken);
        await ExecuteAsync(connectionString, TenantsSql);
        await migrator.MigrateAsync(null, TestContext.Current.CancellationToken);
        return database;
    }

    private static async Task<List<(string Section, bool Enabled, bool InQuotation, bool InOrder, string Title, string Html)>> RowsAsync(
        string connectionString, string tenantId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT section, enabled, in_quotation, in_order, title, content_html FROM quotations.pdf_sections " +
            "WHERE tenant_id = @tenantId ORDER BY section",
            connection);
        command.Parameters.AddWithValue("tenantId", Guid.Parse(tenantId));
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var rows = new List<(string, bool, bool, bool, string, string)>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add((reader.GetString(0), reader.GetBoolean(1), reader.GetBoolean(2), reader.GetBoolean(3), reader.GetString(4),
                reader.GetString(5)));
        }

        return rows;
    }

    private static QuotationsDbContext NewContext(string connectionString) =>
        new(new DbContextOptionsBuilder<QuotationsDbContext>()
            .UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", "quotations"))
            .Options);

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
