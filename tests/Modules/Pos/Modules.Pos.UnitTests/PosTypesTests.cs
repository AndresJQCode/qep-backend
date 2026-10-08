using System.Globalization;
using Modules.Pos.Domain;
using Modules.Quotations.Domain;

namespace Modules.Pos.UnitTests;

public sealed class PosTypesTests
{
    // PosSaleId es la clave de idempotencia y la manda el cliente: un Guid vacío no identifica
    // ningún intento.
    [Fact]
    public void ASaleIdCannotBeEmpty()
    {
        var error = Assert.Throws<PosDomainException>(() => new PosSaleId(Guid.Empty));

        Assert.Equal("pos.sale.id_required", error.Code);
    }

    [Theory]
    [InlineData("2", true)]
    [InlineData("2.00", true)]
    [InlineData("209.30", true)]
    [InlineData("2.005", false)]
    [InlineData("0.001", false)]
    public void HasValidScaleAcceptsAtMostTwoDecimals(string value, bool expected)
    {
        Assert.Equal(expected, PosLimits.HasValidScale(decimal.Parse(value, CultureInfo.InvariantCulture)));
    }

    // Contra los literales del spec y contra la fuente real: POS copia las constantes de
    // Quotations (el dominio de POS no la referencia), así que esta prueba —que sí la ve— es lo
    // único que detecta que las dos copias se separen.
    [Fact]
    public void TheFinalConsumerIsTheSameOneQuotationsUses()
    {
        Assert.Equal("Consumidor final", PosFinalConsumer.Name);
        Assert.Equal("222222222222", PosFinalConsumer.IdentificationNumber);
        Assert.Equal(FinalConsumer.Name, PosFinalConsumer.Name);
        Assert.Equal(FinalConsumer.IdentificationNumber, PosFinalConsumer.IdentificationNumber);
    }
}
