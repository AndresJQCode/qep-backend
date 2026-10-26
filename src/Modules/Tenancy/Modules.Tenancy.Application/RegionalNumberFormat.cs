using System.Globalization;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

/// <summary>
/// Resuelve el formato numérico del tenant para los textos que arma el servidor. Un valor ausente o
/// desconocido cae en coma decimal, el formato colombiano.
/// </summary>
public static class RegionalNumberFormat
{
    public static string Resolve(string? numberFormat) =>
        numberFormat?.Trim() == TenantNumberFormats.DotDecimal
            ? TenantNumberFormats.DotDecimal
            : TenantNumberFormats.CommaDecimal;

    public static (string Thousands, string Decimal) Separators(string? numberFormat) =>
        Resolve(numberFormat) == TenantNumberFormats.DotDecimal
            ? (",", ".")
            : (".", ",");

    public static NumberFormatInfo ToNumberFormatInfo(string? numberFormat)
    {
        var (thousands, decimals) = Separators(numberFormat);
        var info = (NumberFormatInfo)CultureInfo.InvariantCulture.NumberFormat.Clone();
        info.NumberGroupSeparator = thousands;
        info.NumberDecimalSeparator = decimals;
        info.NumberGroupSizes = [3];
        return NumberFormatInfo.ReadOnly(info);
    }
}
