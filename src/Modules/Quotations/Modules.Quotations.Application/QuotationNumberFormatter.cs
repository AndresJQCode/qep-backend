using System.Globalization;

namespace Modules.Quotations.Application;

/// <summary>Formato <c>QUO-2026-0001</c>: el consecutivo con cuatro dígitos, reiniciado cada año
/// porque <see cref="IQuotationNumberGenerator"/> cuenta por (tenant, año).</summary>
internal static class QuotationNumberFormatter
{
    public static string Format(int year, long sequence) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"QUO-{year}-{sequence:D4}");
}
