using Modules.Reporting.Domain;

namespace Modules.Reporting.UnitTests;

/// <summary>
/// La diferencia entre el valor anterior y el nuevo.
///
/// Los dos lados nulos son casos reales, no defensivos: <c>ProductPriceChange</c> deja
/// <c>PreviousValue</c> en null cuando el precio base estaba vacio o la escala no existia, y
/// <c>NewValue</c> en null cuando se limpio o la escala desaparecio. Justamente esas dos filas
/// —el alta y la baja de un precio— son las que mas se miran en el reporte.
/// </summary>
public sealed class PriceChangeDifferenceTests
{
    [Fact]
    public void SubtractsThePreviousValueFromTheNewOne() =>
        Assert.Equal(200m, PriceChangeDifference.Between(1000m, 1200m));

    [Fact]
    public void IsNegativeWhenThePriceWentDown() =>
        Assert.Equal(-150.50m, PriceChangeDifference.Between(1000.50m, 850m));

    [Fact]
    public void TreatsAMissingPreviousValueAsZero() =>
        Assert.Equal(1200m, PriceChangeDifference.Between(null, 1200m));

    [Fact]
    public void TreatsAMissingNewValueAsZero() =>
        Assert.Equal(-1000m, PriceChangeDifference.Between(1000m, null));

    [Fact]
    public void IsZeroWhenBothSidesAreMissing() =>
        Assert.Equal(0m, PriceChangeDifference.Between(null, null));
}
