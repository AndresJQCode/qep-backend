using System.Globalization;

namespace Modules.Quotations.Application;

/// <summary>Formato <c>VEN-2026-0001</c> — mismo criterio que <see cref="QuotationNumberFormatter"/>.</summary>
internal static class SaleNumberFormatter
{
    public static string Format(int year, long sequence) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"VEN-{year}-{sequence:D4}");
}
