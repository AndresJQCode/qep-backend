using Modules.Support.Application;

namespace Modules.Support.UnitTests;

/// <summary>Fase 4b, Tarea 15 (H25): las dos fechas son obligatorias, en <c>YYYY-MM-DD</c>, <c>from</c> no después de <c>to</c> y el rango
/// de hasta 366 días contando los dos extremos.</summary>
public sealed class GetSupportReportValidatorTests
{
    private static IEnumerable<string> Errors(string? from, string? to) =>
        new GetSupportReportValidator()
            .Validate(new GetSupportReportQuery(Guid.CreateVersion7(), from, to))
            .Errors.Select(error => error.PropertyName).Distinct();

    [Fact]
    public void BothDatesAreRequiredFromBeforeToAndAtMost366Days()
    {
        Assert.Empty(Errors("2026-11-01", "2026-11-30"));
        Assert.Empty(Errors("2026-11-13", "2026-11-13"));
        Assert.Empty(Errors("2025-11-14", "2026-11-14"));

        Assert.Equal(["From"], Errors(null, "2026-11-30"));
        Assert.Equal(["To"], Errors("2026-11-01", ""));
        Assert.Equal(["From"], Errors("13/11/2026", "2026-11-30"));
        Assert.Equal(["To"], Errors("2026-11-30", "2026-11-01"));
        Assert.Equal(["To"], Errors("2025-11-13", "2026-11-14"));
    }
}
