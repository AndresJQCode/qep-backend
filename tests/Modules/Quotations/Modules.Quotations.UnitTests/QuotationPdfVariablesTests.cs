using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

// Las variables del contenido configurable del PDF (spec 2026-11-16 §4.3).
public sealed class QuotationPdfVariablesTests
{
    private static readonly QuotationPdfVariableValues Values = new(
        "Comercial Andina S.A.S.",
        "900123456-7",
        "María Pérez",
        "1001",
        "16 de noviembre de 2026",
        "30 de noviembre de 2026",
        "Laura Gómez",
        "$ 1.250.000");

    [Fact]
    public void TheListIsTheContractWithTheConsole() =>
        Assert.Equal(
            [
                ("empresa", "Empresa que factura", "Comercial Andina S.A.S."),
                ("nit_empresa", "NIT de la empresa", "900123456-7"),
                ("cliente", "Cliente", "María Pérez"),
                ("numero", "Número del documento", "1001"),
                ("fecha", "Fecha de emisión", "16 de noviembre de 2026"),
                ("vigencia", "Vigente hasta", "30 de noviembre de 2026"),
                ("asesor", "Asesor", "Laura Gómez"),
                ("total", "Total", "$ 1.250.000"),
            ],
            QuotationPdfVariables.All.Select(variable => (variable.Name, variable.Label, variable.Example)));

    [Fact]
    public void UnknownInFindsEveryTokenOutsideTheList() =>
        Assert.Equal(
            ["ciudad", "Empresa", ""],
            QuotationPdfVariables.UnknownIn("{{ciudad}} y {{Empresa}} y {{}} y {{ empresa }}"));

    [Fact]
    public void ASplitTokenIsUnknown() =>
        Assert.NotEmpty(QuotationPdfVariables.UnknownIn("{{em<strong>pre</strong>sa}}"));

    [Fact]
    public void ANonBreakingSpaceInsideTheBracesIsUnknown()
    {
        Assert.NotEmpty(QuotationPdfVariables.UnknownIn("{{&nbsp;empresa}}"));
        // El saneador puede devolver el carácter y no la entidad: tampoco es un espacio de los que se aceptan.
        Assert.NotEmpty(QuotationPdfVariables.UnknownIn("{{ empresa}}"));
    }

    [Fact]
    public void FillResolvesEveryVariable() =>
        Assert.Equal(
            "Comercial Andina S.A.S. 900123456-7 María Pérez 1001 16 de noviembre de 2026 30 de noviembre de 2026 Laura Gómez $ 1.250.000 Comercial Andina S.A.S.",
            QuotationPdfVariables.Fill(
                "{{empresa}} {{nit_empresa}} {{cliente}} {{numero}} {{fecha}} {{vigencia}} {{asesor}} {{total}} {{ empresa }}",
                Values));

    [Fact]
    public void AMissingValueIsEmpty() =>
        Assert.Equal("Para .", QuotationPdfVariables.Fill("Para {{empresa}}.", Values with { Company = null }));

    [Fact]
    public void AnUnknownTokenStaysLiteral() =>
        Assert.Equal("{{ciudad}} 1001", QuotationPdfVariables.Fill("{{ciudad}} {{numero}}", Values));
}
