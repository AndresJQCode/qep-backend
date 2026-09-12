using System.Globalization;
using Modules.Quotations.Application;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// Los bordes del rango de la exportacion. "Un año" se mide con <c>AddYears(1)</c> y no con 365
/// dias: asi el mismo rango vale igual en un año bisiesto, y desde un 29 de febrero el año
/// termina el 28.
/// </summary>
public sealed class ExportQuotationsValidatorTests
{
    private readonly ExportQuotationsValidator _validator = new();

    [Theory]
    [InlineData("2025-01-01", "2026-01-01")]
    [InlineData("2024-02-29", "2025-02-28")]
    [InlineData("2026-09-12", "2026-09-12")]
    public void AcceptsARangeOfAtMostOneYear(string from, string to)
    {
        var result = _validator.Validate(NewQuery(Date(from), Date(to)));

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("2025-01-01", "2026-01-02")]
    [InlineData("2024-02-29", "2025-03-01")]
    public void RejectsARangeLongerThanOneYear(string from, string to)
    {
        var result = _validator.Validate(NewQuery(Date(from), Date(to)));

        var failure = Assert.Single(result.Errors);
        Assert.Equal("CreatedTo", failure.PropertyName);
    }

    [Fact]
    public void RejectsCreatedToBeforeCreatedFrom()
    {
        var result = _validator.Validate(
            NewQuery(new DateOnly(2026, 9, 12), new DateOnly(2026, 9, 11)));

        var failure = Assert.Single(result.Errors);
        Assert.Equal("CreatedTo", failure.PropertyName);
    }

    [Fact]
    public void RequiresBothDates()
    {
        var result = _validator.Validate(NewQuery(null, null));

        Assert.Equal(
            ["CreatedFrom", "CreatedTo"],
            result.Errors.Select(failure => failure.PropertyName).Order(StringComparer.Ordinal));
    }

    private static DateOnly Date(string value) =>
        DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static ExportQuotationsQuery NewQuery(DateOnly? from, DateOnly? to) =>
        new(Guid.CreateVersion7(), null, null, null, from, to, null, null);
}
