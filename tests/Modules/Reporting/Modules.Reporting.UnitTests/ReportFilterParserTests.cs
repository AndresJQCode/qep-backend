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
    [InlineData("Converted", QuotationStatusFilter.Converted)]
    public void ParsesTheFiveQuotationStatuses(string value, QuotationStatusFilter expected) =>
        Assert.Equal(expected, ReportFilterParser.ParseQuotationStatus(value));

    /// <summary>
    /// `Approved` dejó de ser un estado de cotización: convertirla en pedido la deja en
    /// `Converted`. `Approved` es un estado del pedido (`OrderStatus`), no de la cotización.
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

    /// <summary>El estado de pago de un pedido es un enum distinto y sigue teniendo sus tres
    /// valores: bajar `Approved` de las cotizaciones no lo tocó.</summary>
    [Theory]
    [InlineData("FullPaymentReceived", OrderPaymentStatusFilter.FullPaymentReceived)]
    [InlineData("PartialPaymentReceived", OrderPaymentStatusFilter.PartialPaymentReceived)]
    [InlineData("PaymentPending", OrderPaymentStatusFilter.PaymentPending)]
    public void ParsesTheThreePaymentStatuses(string value, OrderPaymentStatusFilter expected) =>
        Assert.Equal(expected, ReportFilterParser.ParsePaymentStatus(value));

    [Theory]
    [InlineData("PriceBase", PriceChangeField.PriceBase)]
    [InlineData("ScaleDiscount", PriceChangeField.ScaleDiscount)]
    public void ParsesTheTwoPriceChangeFields(string value, PriceChangeField expected) =>
        Assert.Equal(expected, ReportFilterParser.ParsePriceChangeField(value));

    /// <summary>Los cuatro orígenes del reporte de pedidos: los tres de <c>OrderSource</c> más
    /// <c>Pos</c>, que es una venta de mostrador y no un pedido (spec 2026-10-23 §2).</summary>
    [Theory]
    [InlineData("Quotation", OrderSourceFilter.Quotation)]
    [InlineData("Manual", OrderSourceFilter.Manual)]
    [InlineData("Shopify", OrderSourceFilter.Shopify)]
    [InlineData("Pos", OrderSourceFilter.Pos)]
    public void ParsesEveryOrderSource(string value, OrderSourceFilter expected)
    {
        Assert.True(ReportFilterParser.TryParseSource(value, out var parsed));
        Assert.Equal(expected, parsed);
        Assert.Equal(expected, ReportFilterParser.ParseSource(value));
    }

    [Theory]
    [InlineData("pos")]
    [InlineData("Store")]
    public void RejectsAnUnknownOrLowercaseSource(string value)
    {
        Assert.False(ReportFilterParser.TryParseSource(value, out _));
        var error = Assert.Throws<ReportingDomainException>(() => ReportFilterParser.ParseSource(value));
        Assert.Equal("reporting.filter.invalid", error.Code);
    }

    [Fact]
    public void BlankSourceIsNoFilter() =>
        Assert.Null(ReportFilterParser.ParseSource("  "));

    [Theory]
    [InlineData("PriceBaseUsd")]
    [InlineData("PriceBaseCop")]
    public void TheRetiredPerCurrencyFieldsAreNoLongerValid(string value) =>
        Assert.False(ReportFilterParser.TryParsePriceChangeField(value, out _));
}
