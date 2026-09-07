using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// El PDF de una cotización es un artefacto derivado: se puede volver a generar en cualquier
/// momento a partir del agregado. Lo único que esta entidad decide es **cuándo hace falta**, y
/// lo decide contra <c>Quotation.Version</c>, que ya se incrementa en cada cambio del agregado.
/// Sin eso, cada envío y cada exportación pagarían una llamada a `qcode-pdf` para producir un
/// documento idéntico al anterior.
/// </summary>
public sealed class QuotationPdfTests
{
    private static readonly QuotationId Quotation = QuotationId.New();
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void APdfGeneratedForTheCurrentVersionIsStillGood()
    {
        var pdf = QuotationPdf.Generate(Quotation, Tenant, "quotations/tenants/x/a.pdf", 7, Now);

        Assert.False(pdf.IsStaleFor(7));
    }

    [Fact]
    public void APdfBecomesStaleWhenTheQuotationChanges()
    {
        var pdf = QuotationPdf.Generate(Quotation, Tenant, "quotations/tenants/x/a.pdf", 7, Now);

        Assert.True(pdf.IsStaleFor(8));
    }

    // No se compara con `!=` sino con `<`: si el PDF quedara adelante de la cotización -- una
    // restauración de base, una escritura fuera de orden -- regenerarlo no arregla nada y
    // volvería a hacerlo en cada pedido, para siempre.
    [Fact]
    public void APdfAheadOfTheQuotationIsNotConsideredStale()
    {
        var pdf = QuotationPdf.Generate(Quotation, Tenant, "quotations/tenants/x/a.pdf", 9, Now);

        Assert.False(pdf.IsStaleFor(8));
    }

    [Fact]
    public void RegeneratingPointsAtTheNewObjectAndVersion()
    {
        var pdf = QuotationPdf.Generate(Quotation, Tenant, "quotations/tenants/x/a.pdf", 7, Now);
        var later = Now.AddHours(3);

        pdf.Regenerate("quotations/tenants/x/b.pdf", 9, later);

        Assert.Equal("quotations/tenants/x/b.pdf", pdf.StorageKey);
        Assert.Equal(9, pdf.QuotationVersion);
        Assert.Equal(later, pdf.GeneratedAt);
        Assert.False(pdf.IsStaleFor(9));
    }
}
