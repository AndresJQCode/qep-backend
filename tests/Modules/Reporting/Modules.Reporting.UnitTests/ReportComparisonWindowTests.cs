using Modules.Reporting.Application;

namespace Modules.Reporting.UnitTests;

/// <summary>
/// La ventana contra la que se compara un resumen.
///
/// El "vs. periodo anterior" de un KPI no significa nada si no se dice contra que: aca la regla
/// es la ventana **de la misma longitud, inmediatamente anterior**, y las dos puntas son
/// inclusivas igual que en el filtro (<c>ReportDateRange</c>). Un mes calendario anterior seria
/// otra regla y daria otro numero — por eso esto se prueba solo y no dentro del handler.
/// </summary>
public sealed class ReportComparisonWindowTests
{
    [Fact]
    public void AMonthComparesAgainstThePrecedingMonthOfTheSameLength()
    {
        var window = ReportComparisonWindow.Preceding(
            new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));

        Assert.NotNull(window);
        Assert.Equal(new DateOnly(2025, 12, 1), window.Value.From);
        Assert.Equal(new DateOnly(2025, 12, 31), window.Value.To);
    }

    /// <summary>
    /// Enero tiene 31 dias y febrero 28: la ventana anterior a febrero son **28 dias**, no "el
    /// mes anterior". Comparar 28 dias contra 31 inflaria el periodo viejo un 10%.
    /// </summary>
    [Fact]
    public void TheWindowKeepsItsLengthAndDoesNotJumpToTheCalendarMonth()
    {
        var window = ReportComparisonWindow.Preceding(
            new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28));

        Assert.NotNull(window);
        Assert.Equal(new DateOnly(2026, 1, 4), window.Value.From);
        Assert.Equal(new DateOnly(2026, 1, 31), window.Value.To);
    }

    [Fact]
    public void ASingleDayComparesAgainstTheDayBefore()
    {
        var window = ReportComparisonWindow.Preceding(
            new DateOnly(2026, 1, 15), new DateOnly(2026, 1, 15));

        Assert.NotNull(window);
        Assert.Equal(new DateOnly(2026, 1, 14), window.Value.From);
        Assert.Equal(new DateOnly(2026, 1, 14), window.Value.To);
    }

    /// <summary>
    /// Sin las dos puntas no hay longitud, y sin longitud no hay periodo anterior. Inventar uno
    /// —"los ultimos 30 dias"— seria comparar contra algo que el usuario no pidio.
    /// </summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void AnOpenEndedRangeHasNoPrecedingWindow(bool hasFrom, bool hasTo)
    {
        var window = ReportComparisonWindow.Preceding(
            hasFrom ? new DateOnly(2026, 1, 1) : null,
            hasTo ? new DateOnly(2026, 1, 31) : null);

        Assert.Null(window);
    }

    /// <summary>Un rango dado vuelto —<c>to</c> antes que <c>from</c>— no tiene longitud; el
    /// validador ya lo rechaza, y aca no se inventa una ventana negativa.</summary>
    [Fact]
    public void AnInvertedRangeHasNoPrecedingWindow()
    {
        var window = ReportComparisonWindow.Preceding(
            new DateOnly(2026, 1, 31), new DateOnly(2026, 1, 1));

        Assert.Null(window);
    }
}
