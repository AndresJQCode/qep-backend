using Modules.Quotations.Domain;
using Modules.Tenancy.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// El PDF de una cotización es un artefacto derivado: se puede volver a generar en cualquier
/// momento a partir del agregado. Lo único que esta entidad decide es **cuándo hace falta**, y
/// lo decide contra `Quotation.Version` y contra `LogoFileId` del tenant (spec 2026-09-19,
/// decisión 10), que ya se incrementa/cambia por su cuenta. Sin eso, cada envío y cada
/// exportación pagarían una llamada a `qcode-pdf` para producir un documento idéntico al anterior.
/// </summary>
public sealed class QuotationPdfTests
{
    private static readonly QuotationId Quotation = QuotationId.New();
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly Guid LogoFileId = Guid.CreateVersion7();
    private static readonly Guid OtherLogoFileId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void APdfGeneratedForTheCurrentVersionIsStillGood()
    {
        var pdf = QuotationPdf.Generate(Quotation, Tenant, "quotations/tenants/x/a.pdf", 7, null, TenantNumberFormats.CommaDecimal, Now);

        Assert.False(pdf.IsStaleFor(7, null, TenantNumberFormats.CommaDecimal));
    }

    [Fact]
    public void APdfBecomesStaleWhenTheQuotationChanges()
    {
        var pdf = QuotationPdf.Generate(Quotation, Tenant, "quotations/tenants/x/a.pdf", 7, null, TenantNumberFormats.CommaDecimal, Now);

        Assert.True(pdf.IsStaleFor(8, null, TenantNumberFormats.CommaDecimal));
    }

    // No se compara con `!=` sino con `<`: si el PDF quedara adelante de la cotización -- una
    // restauración de base, una escritura fuera de orden -- regenerarlo no arregla nada y
    // volvería a hacerlo en cada pedido, para siempre.
    [Fact]
    public void APdfAheadOfTheQuotationIsNotConsideredStale()
    {
        var pdf = QuotationPdf.Generate(Quotation, Tenant, "quotations/tenants/x/a.pdf", 9, null, TenantNumberFormats.CommaDecimal, Now);

        Assert.False(pdf.IsStaleFor(8, null, TenantNumberFormats.CommaDecimal));
    }

    [Fact]
    public void RegeneratingPointsAtTheNewObjectAndVersion()
    {
        var pdf = QuotationPdf.Generate(Quotation, Tenant, "quotations/tenants/x/a.pdf", 7, null, TenantNumberFormats.CommaDecimal, Now);
        var later = Now.AddHours(3);

        pdf.Regenerate("quotations/tenants/x/b.pdf", 9, null, TenantNumberFormats.CommaDecimal, later);

        Assert.Equal("quotations/tenants/x/b.pdf", pdf.StorageKey);
        Assert.Equal(9, pdf.QuotationVersion);
        Assert.Equal(later, pdf.GeneratedAt);
        Assert.False(pdf.IsStaleFor(9, null, TenantNumberFormats.CommaDecimal));
    }

    // Decisión 10 del spec 2026-09-19: el logo invalida la caché igual que la versión, comparado
    // por `!=` porque no tiene orden — a diferencia de `QuotationVersion`, que sólo sube.
    [Fact]
    public void APdfWithAnotherLogoIsStale()
    {
        var pdf = QuotationPdf.Generate(Quotation, Tenant, "quotations/tenants/x/a.pdf", 7, LogoFileId, TenantNumberFormats.CommaDecimal, Now);

        Assert.True(pdf.IsStaleFor(7, OtherLogoFileId, TenantNumberFormats.CommaDecimal));
    }

    [Fact]
    public void APdfWithTheSameLogoIsNotStale()
    {
        var pdf = QuotationPdf.Generate(Quotation, Tenant, "quotations/tenants/x/a.pdf", 7, LogoFileId, TenantNumberFormats.CommaDecimal, Now);

        Assert.False(pdf.IsStaleFor(7, LogoFileId, TenantNumberFormats.CommaDecimal));
    }

    [Fact]
    public void APdfWithoutLogoIsStaleOnceTheTenantHasOne()
    {
        var pdf = QuotationPdf.Generate(Quotation, Tenant, "quotations/tenants/x/a.pdf", 7, null, TenantNumberFormats.CommaDecimal, Now);

        Assert.True(pdf.IsStaleFor(7, LogoFileId, TenantNumberFormats.CommaDecimal));
    }

    // "Y la vuelta" (spec, § Pruebas): un tenant que tenía logo y lo quita también invalida.
    [Fact]
    public void APdfWithALogoIsStaleOnceTheTenantRemovesIt()
    {
        var pdf = QuotationPdf.Generate(Quotation, Tenant, "quotations/tenants/x/a.pdf", 7, LogoFileId, TenantNumberFormats.CommaDecimal, Now);

        Assert.True(pdf.IsStaleFor(7, null, TenantNumberFormats.CommaDecimal));
    }

    // El formato numérico es parte de lo que se imprimió: cambiarlo en el tenant tiene que vencer el
    // PDF guardado, o el documento seguiría saliendo con los separadores anteriores.
    [Fact]
    public void APdfPrintedWithAnotherNumberFormatIsStale()
    {
        var pdf = QuotationPdf.Generate(
            Quotation, Tenant, "quotations/tenants/x/a.pdf", 7, null, TenantNumberFormats.CommaDecimal, Now);

        Assert.True(pdf.IsStaleFor(7, null, TenantNumberFormats.DotDecimal));
    }

    [Fact]
    public void APdfPrintedWithTheSameNumberFormatIsNotStale()
    {
        var pdf = QuotationPdf.Generate(
            Quotation, Tenant, "quotations/tenants/x/a.pdf", 7, null, TenantNumberFormats.DotDecimal, Now);

        Assert.False(pdf.IsStaleFor(7, null, TenantNumberFormats.DotDecimal));
    }

    // Las filas anteriores a la columna quedan en NULL: se imprimieron con el formato colombiano, que
    // era el único. La prueba usa la constante de Tenancy a propósito: es lo que ata el literal del dominio.
    [Fact]
    public void ALegacyPdfWithoutFormatCountsAsCommaDecimal()
    {
        var pdf = QuotationPdf.Generate(
            Quotation, Tenant, "quotations/tenants/x/a.pdf", 7, null, numberFormat: null, Now);

        Assert.False(pdf.IsStaleFor(7, null, TenantNumberFormats.CommaDecimal));
        Assert.True(pdf.IsStaleFor(7, null, TenantNumberFormats.DotDecimal));
    }

    [Fact]
    public void RegeneratingRecordsTheNewNumberFormat()
    {
        var pdf = QuotationPdf.Generate(
            Quotation, Tenant, "quotations/tenants/x/a.pdf", 7, null, TenantNumberFormats.CommaDecimal, Now);

        pdf.Regenerate("quotations/tenants/x/b.pdf", 7, null, TenantNumberFormats.DotDecimal, Now.AddHours(1));

        Assert.Equal(TenantNumberFormats.DotDecimal, pdf.NumberFormat);
        Assert.False(pdf.IsStaleFor(7, null, TenantNumberFormats.DotDecimal));
    }
}
