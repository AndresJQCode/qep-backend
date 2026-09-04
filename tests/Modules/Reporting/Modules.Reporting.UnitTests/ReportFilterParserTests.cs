using Modules.Reporting.Domain;

namespace Modules.Reporting.UnitTests;

/// <summary>
/// El parseo de los filtros que llegan por query string. Un valor que no se reconoce falla en vez
/// de caer en un default: elegir un estado en silencio le cambia el reporte a quien lo pidió sin
/// que se entere.
/// </summary>
public sealed class ReportFilterParserTests
{
    [Theory]
    [InlineData("Draft", QuotationStatusFilter.Draft)]
    [InlineData("Sent", QuotationStatusFilter.Sent)]
    [InlineData("Expired", QuotationStatusFilter.Expired)]
    [InlineData("Voided", QuotationStatusFilter.Voided)]
    public void ParsesTheFourQuotationStatuses(string value, QuotationStatusFilter expected) =>
        Assert.Equal(expected, ReportFilterParser.ParseQuotationStatus(value));

    /// <summary>
    /// `Approved` dejó de ser un estado de cotización: convertirla en venta la deja en `Sent`, y
    /// la única señal de la conversión es que exista una `Sale` apuntándola 1:1.
    ///
    /// Esta prueba existe porque el enum del filtro se redeclara acá en vez de referenciar
    /// `QuotationStatus` —el dominio de un módulo no referencia el de otro—, así que el
    /// compilador no puede avisar cuando los dos se desalinean. Y ya se desalinearon una vez: el
    /// módulo nació con los cinco estados y una rama paralela bajó el quinto.
    /// </summary>
    [Fact]
    public void ApprovedIsNotAQuotationStatusAnyMore() =>
        Assert.Throws<ReportingDomainException>(
            () => ReportFilterParser.ParseQuotationStatus("Approved"));

    [Fact]
    public void AnAbsentFilterParsesToNull() =>
        Assert.Null(ReportFilterParser.ParseQuotationStatus(null));

    [Fact]
    public void AnEmptyFilterParsesToNull() =>
        Assert.Null(ReportFilterParser.ParseQuotationStatus("   "));

    /// <summary>La comparación es sensible a mayúsculas: los valores del contrato viajan en
    /// PascalCase, que es como los serializa el resto de la API.</summary>
    [Theory]
    [InlineData("sent")]
    [InlineData("SENT")]
    [InlineData("Enviada")]
    [InlineData("NotAStatus")]
    public void AnUnknownQuotationStatusFails(string value) =>
        Assert.Throws<ReportingDomainException>(
            () => ReportFilterParser.ParseQuotationStatus(value));

    /// <summary>El estado de pago de una venta es un enum distinto y sigue teniendo sus tres
    /// valores: bajar `Approved` de las cotizaciones no lo tocó.</summary>
    [Theory]
    [InlineData("FullPaymentReceived", SalePaymentStatusFilter.FullPaymentReceived)]
    [InlineData("PartialPaymentReceived", SalePaymentStatusFilter.PartialPaymentReceived)]
    [InlineData("PaymentPending", SalePaymentStatusFilter.PaymentPending)]
    public void ParsesTheThreePaymentStatuses(string value, SalePaymentStatusFilter expected) =>
        Assert.Equal(expected, ReportFilterParser.ParsePaymentStatus(value));

    [Theory]
    [InlineData("PriceBaseUsd", PriceChangeField.PriceBaseUsd)]
    [InlineData("PriceBaseCop", PriceChangeField.PriceBaseCop)]
    [InlineData("ScaleDiscount", PriceChangeField.ScaleDiscount)]
    public void ParsesTheThreePriceChangeFields(string value, PriceChangeField expected) =>
        Assert.Equal(expected, ReportFilterParser.ParsePriceChangeField(value));
}
