using Modules.Quotations.Domain;

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
        var pdf = QuotationPdf.Generate(Quotation, Tenant, "quotations/tenants/x/a.pdf", 7, null, Now);

        Assert.False(pdf.IsStaleFor(7, null));
    }

    [Fact]
    public void APdfBecomesStaleWhenTheQuotationChanges()
    {
        var pdf = QuotationPdf.Generate(Quotation, Tenant, "quotations/tenants/x/a.pdf", 7, null, Now);

        Assert.True(pdf.IsStaleFor(8, null));
    }

    // No se compara con `!=` sino con `<`: si el PDF quedara adelante de la cotización -- una
    // restauración de base, una escritura fuera de orden -- regenerarlo no arregla nada y
    // volvería a hacerlo en cada pedido, para siempre.
    [Fact]
    public void APdfAheadOfTheQuotationIsNotConsideredStale()
    {
        var pdf = QuotationPdf.Generate(Quotation, Tenant, "quotations/tenants/x/a.pdf", 9, null, Now);

        Assert.False(pdf.IsStaleFor(8, null));
    }

    [Fact]
    public void RegeneratingPointsAtTheNewObjectAndVersion()
    {
        var pdf = QuotationPdf.Generate(Quotation, Tenant, "quotations/tenants/x/a.pdf", 7, null, Now);
        var later = Now.AddHours(3);

        pdf.Regenerate("quotations/tenants/x/b.pdf", 9, null, later);

        Assert.Equal("quotations/tenants/x/b.pdf", pdf.StorageKey);
        Assert.Equal(9, pdf.QuotationVersion);
        Assert.Equal(later, pdf.GeneratedAt);
        Assert.False(pdf.IsStaleFor(9, null));
    }

    // Decisión 10 del spec 2026-09-19: el logo invalida la caché igual que la versión, comparado
    // por `!=` porque no tiene orden — a diferencia de `QuotationVersion`, que sólo sube.
    [Fact]
    public void APdfWithAnotherLogoIsStale()
    {
        var pdf = QuotationPdf.Generate(Quotation, Tenant, "quotations/tenants/x/a.pdf", 7, LogoFileId, Now);

        Assert.True(pdf.IsStaleFor(7, OtherLogoFileId));
    }

    [Fact]
    public void APdfWithTheSameLogoIsNotStale()
    {
        var pdf = QuotationPdf.Generate(Quotation, Tenant, "quotations/tenants/x/a.pdf", 7, LogoFileId, Now);

        Assert.False(pdf.IsStaleFor(7, LogoFileId));
    }

    [Fact]
    public void APdfWithoutLogoIsStaleOnceTheTenantHasOne()
    {
        var pdf = QuotationPdf.Generate(Quotation, Tenant, "quotations/tenants/x/a.pdf", 7, null, Now);

        Assert.True(pdf.IsStaleFor(7, LogoFileId));
    }

    // "Y la vuelta" (spec, § Pruebas): un tenant que tenía logo y lo quita también invalida.
    [Fact]
    public void APdfWithALogoIsStaleOnceTheTenantRemovesIt()
    {
        var pdf = QuotationPdf.Generate(Quotation, Tenant, "quotations/tenants/x/a.pdf", 7, LogoFileId, Now);

        Assert.True(pdf.IsStaleFor(7, null));
    }
}
