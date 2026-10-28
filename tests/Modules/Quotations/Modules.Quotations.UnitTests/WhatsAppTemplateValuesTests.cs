using Modules.Quotations.Application;
using Modules.Tenancy.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>Lo que dicen las variables de la plantilla: iguales para Zenvia y para Meta.</summary>
public sealed class WhatsAppTemplateValuesTests
{
    [Fact]
    public void TotalUsesTheCurrencyAndTheTenantSeparators()
    {
        Assert.Equal("$ 2.450.000", WhatsAppTemplateValues.Total(2450000m, "COP", null));
        Assert.Equal("€ 2,450.50", WhatsAppTemplateValues.Total(2450.5m, "EUR", TenantNumberFormats.DotDecimal));
        Assert.Equal("€ 2.450,50", WhatsAppTemplateValues.Total(2450.5m, "EUR", null));
    }

    [Fact]
    public void ValidUntilIsTheLongSpanishDate() =>
        Assert.Equal("30 de septiembre de 2026", WhatsAppTemplateValues.ValidUntil(new DateOnly(2026, 9, 30)));

    [Fact]
    public void DocumentFileNameCarriesTheQuotationNumber() =>
        Assert.Equal("Cotizacion-COT-000123.pdf", WhatsAppTemplateValues.DocumentFileName("COT-000123"));
}
