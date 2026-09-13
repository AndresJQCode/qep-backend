using System.Globalization;
using Modules.Quotations.Application;

namespace Modules.Quotations.UnitTests;

/// <summary>El mismo rango que cotizaciones (D3), sobre la fecha de la venta:
/// <c>convertedFrom</c>/<c>convertedTo</c>.</summary>
public sealed class ExportSalesValidatorTests
{
    private readonly ExportSalesValidator _validator = new();

    [Theory]
    [InlineData("2025-01-01", "2026-01-01")]
    [InlineData("2024-02-29", "2025-02-28")]
    [InlineData("2026-09-12", "2026-09-12")]
    public void AcceptsARangeOfAtMostOneYear(string from, string to)
    {
        Assert.True(_validator.Validate(NewCommand(Date(from), Date(to))).IsValid);
    }

    [Theory]
    [InlineData("2025-01-01", "2026-01-02")]
    [InlineData("2024-02-29", "2025-03-01")]
    public void RejectsARangeLongerThanOneYear(string from, string to)
    {
        var failure = Assert.Single(_validator.Validate(NewCommand(Date(from), Date(to))).Errors);
        Assert.Equal("ConvertedTo", failure.PropertyName);
    }

    [Fact]
    public void RejectsConvertedToBeforeConvertedFrom()
    {
        var failure = Assert.Single(_validator.Validate(
            NewCommand(new DateOnly(2026, 9, 12), new DateOnly(2026, 9, 11))).Errors);
        Assert.Equal("ConvertedTo", failure.PropertyName);
    }

    [Fact]
    public void RequiresBothDates()
    {
        Assert.Equal(
            ["ConvertedFrom", "ConvertedTo"],
            _validator.Validate(NewCommand(null, null)).Errors
                .Select(failure => failure.PropertyName)
                .Order(StringComparer.Ordinal));
    }

    private static DateOnly Date(string value) =>
        DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static ExportSalesCommand NewCommand(DateOnly? from, DateOnly? to) =>
        new(Guid.CreateVersion7(), null, null, null, null, from, to, null, null);
}
