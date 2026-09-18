using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// El formato del número de documento (spec 2026-09-17 de numeración configurable). Es puro: recibe
/// el formato, el año y el consecutivo, y devuelve el texto. Los cuatro casos de la tabla del spec,
/// más los rangos que la base también hace cumplir con un CHECK.
/// </summary>
public sealed class DocumentNumberFormatterTests
{
    // Sin fila en la base, el comportamiento de siempre: ningún tenant existente cambia.
    [Fact]
    public void TheDefaultQuotationFormatKeepsTodaysNumber()
    {
        var format = DocumentNumberFormat.DefaultFor(DocumentNumberType.Quotation);

        Assert.Equal("QUO-2026-0001", DocumentNumberFormatter.Format(format, 2026, 1L));
    }

    [Fact]
    public void TheDefaultOrderFormatKeepsTodaysNumber()
    {
        var format = DocumentNumberFormat.DefaultFor(DocumentNumberType.Order);

        Assert.Equal("PED-2026-0001", DocumentNumberFormatter.Format(format, 2026, 1L));
    }

    // Cobertura que tenía OrderNumberFormatterTests: el relleno a cuatro dígitos con un consecutivo
    // de dos cifras.
    [Fact]
    public void TheDefaultOrderFormatPadsToFourDigits()
    {
        var format = DocumentNumberFormat.DefaultFor(DocumentNumberType.Order);

        Assert.Equal("PED-2026-0042", DocumentNumberFormatter.Format(format, 2026, 42L));
    }

    // El caso que motivó el spec: el cliente PW viene de otro sistema y sigue su propio consecutivo,
    // sin año y sin relleno.
    [Fact]
    public void AFormatWithoutYearIsJustPrefixAndSequence()
    {
        var format = DocumentNumberFormat.Create("PW", includeYear: false, yearSeparator: "", minDigits: 1);

        Assert.Equal("PW234235", DocumentNumberFormatter.Format(format, 2026, 234_235L));
    }

    [Fact]
    public void AFormatWithYearAndSixDigitsPadsToItsWidth()
    {
        var format = DocumentNumberFormat.Create("PW-", includeYear: true, yearSeparator: "-", minDigits: 6);

        Assert.Equal("PW-2026-000007", DocumentNumberFormatter.Format(format, 2026, 7L));
    }

    // El consecutivo nunca se recorta: min_digits es un mínimo, no un ancho fijo.
    [Fact]
    public void ASequenceWiderThanMinDigitsIsNotTruncated()
    {
        var format = DocumentNumberFormat.Create("PED-", includeYear: true, yearSeparator: "-", minDigits: 4);

        Assert.Equal("PED-2027-12345", DocumentNumberFormatter.Format(format, 2027, 12_345L));
    }

    [Fact]
    public void TheYearSeparatorCanBeASlash()
    {
        var format = DocumentNumberFormat.Create("FV", includeYear: true, yearSeparator: "/", minDigits: 3);

        Assert.Equal("FV2026/042", DocumentNumberFormatter.Format(format, 2026, 42L));
    }

    /// <summary>
    /// El formateador no recorta ni valida el largo: el que decide es el dominio, que ya tiene el
    /// código de error y el 422 (<c>order.order.number_too_long</c>). Ese consecutivo se pierde y
    /// queda un hueco en la serie — lo mismo que ya pasa con el CUC.
    /// </summary>
    [Fact]
    public void ANumberLongerThanTwentyCharactersIsRejectedByTheDomain()
    {
        var format = DocumentNumberFormat.Create(
            "ABCDEFGHIJ", includeYear: true, yearSeparator: "-", minDigits: 10);

        var number = DocumentNumberFormatter.Format(format, 2026, 1L);

        Assert.Equal("ABCDEFGHIJ2026-0000000001", number);
        Assert.True(number.Length > Order.OrderNumberMaxLength);
        Assert.True(number.Length > Quotation.QuotationNumberMaxLength);

        var error = Assert.Throws<QuotationsDomainException>(() =>
            Order.Create(
                OrderId.New(),
                Guid.CreateVersion7(),
                number,
                QuotationId.New(),
                OrderPaymentStatus.FullPaymentReceived,
                null,
                new MemberId(Guid.CreateVersion7()),
                [new OrderPaymentProofInput(Guid.CreateVersion7(), 1m)],
                DateTimeOffset.UtcNow));

        Assert.Equal("order.order.number_too_long", error.Code);
    }

    [Theory]
    [InlineData("PW ")]          // el espacio no está en [A-Za-z0-9-]
    [InlineData("P_W")]
    [InlineData("ABCDEFGHIJK")]  // once caracteres
    public void AnInvalidPrefixIsRejected(string prefix)
    {
        var error = Assert.Throws<QuotationsDomainException>(() =>
            DocumentNumberFormat.Create(prefix, includeYear: false, yearSeparator: "", minDigits: 1));

        Assert.Equal("quotation.numbering.format_invalid", error.Code);
    }

    [Theory]
    [InlineData(".")]
    [InlineData("--")]
    [InlineData(" ")]
    public void AnInvalidYearSeparatorIsRejected(string yearSeparator)
    {
        var error = Assert.Throws<QuotationsDomainException>(() =>
            DocumentNumberFormat.Create("PW", includeYear: true, yearSeparator, minDigits: 1));

        Assert.Equal("quotation.numbering.format_invalid", error.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public void AnInvalidMinDigitsIsRejected(int minDigits)
    {
        var error = Assert.Throws<QuotationsDomainException>(() =>
            DocumentNumberFormat.Create("PW", includeYear: false, yearSeparator: "", minDigits));

        Assert.Equal("quotation.numbering.format_invalid", error.Code);
    }

    // Un prefijo vacío es válido: el número es sólo el año y el consecutivo.
    [Fact]
    public void AnEmptyPrefixIsAccepted()
    {
        var format = DocumentNumberFormat.Create("", includeYear: true, yearSeparator: "-", minDigits: 4);

        Assert.Equal("2026-0001", DocumentNumberFormatter.Format(format, 2026, 1L));
    }
}
