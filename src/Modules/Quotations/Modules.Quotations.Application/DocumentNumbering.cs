using System.Globalization;
using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>Los dos documentos con consecutivo propio. Es el <c>document_type</c> de
/// <c>quotations.document_numbering_formats</c>; el adaptador traduce cada valor a su texto.</summary>
public enum DocumentNumberType
{
    Quotation,
    Order,
}

/// <summary>
/// Cómo se arma el número de un documento de un tenant (spec 2026-09-17): el prefijo, si lleva el
/// año, con qué separador, y con cuántos dígitos mínimos se rellena el consecutivo.
///
/// Es un valor puro y validado: quien lo construye pasó por <see cref="Create"/>, así que el
/// formateador no vuelve a comprobar nada. Los mismos tres rangos van como <c>CHECK</c> en la base
/// —es configuración que se escribe a mano y el <c>CHECK</c> es la única red que no depende de quién
/// corra el SQL—, pero se validan también acá: una base restaurada o migrada a mano puede traer una
/// fila que el <c>CHECK</c> nunca vio.
/// </summary>
public sealed record DocumentNumberFormat
{
    public const int PrefixMaxLength = 10;

    public const int MinDigitsLowerBound = 1;

    public const int MinDigitsUpperBound = 10;

    /// <summary>Los cuatro dígitos que emiten hoy <c>QUO-2026-0001</c> y <c>PED-2026-0001</c>.</summary>
    public const int DefaultMinDigits = 4;

    public const string DefaultYearSeparator = "-";

    private const string InvalidFormatCode = "quotation.numbering.format_invalid";

    private static readonly string[] AllowedYearSeparators = ["", "-", "/"];

    private DocumentNumberFormat(string prefix, bool includeYear, string yearSeparator, int minDigits)
    {
        Prefix = prefix;
        IncludeYear = includeYear;
        YearSeparator = yearSeparator;
        MinDigits = minDigits;
    }

    /// <summary>De 0 a 10 caracteres en <c>[A-Za-z0-9-]</c>. Vacío es válido.</summary>
    public string Prefix { get; }

    public bool IncludeYear { get; }

    /// <summary>Sólo se usa si <see cref="IncludeYear"/>. Vacío, <c>-</c> o <c>/</c>.</summary>
    public string YearSeparator { get; }

    /// <summary>Relleno con ceros a la izquierda. Es un mínimo, no un ancho: un consecutivo más
    /// largo sale entero.</summary>
    public int MinDigits { get; }

    public static DocumentNumberFormat Create(
        string prefix,
        bool includeYear,
        string yearSeparator,
        int minDigits)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        ArgumentNullException.ThrowIfNull(yearSeparator);

        if (prefix.Length > PrefixMaxLength || !prefix.All(IsAllowedPrefixCharacter))
        {
            throw new QuotationsDomainException(
                InvalidFormatCode,
                $"The document number prefix must be at most {PrefixMaxLength} characters " +
                "of letters, digits or hyphens.");
        }

        if (!AllowedYearSeparators.Contains(yearSeparator, StringComparer.Ordinal))
        {
            throw new QuotationsDomainException(
                InvalidFormatCode,
                "The document number year separator must be empty, '-' or '/'.");
        }

        if (minDigits is < MinDigitsLowerBound or > MinDigitsUpperBound)
        {
            throw new QuotationsDomainException(
                InvalidFormatCode,
                $"The document number minimum digits must be between {MinDigitsLowerBound} " +
                $"and {MinDigitsUpperBound}.");
        }

        return new DocumentNumberFormat(prefix, includeYear, yearSeparator, minDigits);
    }

    /// <summary>El formato de un tenant sin fila: lo que el código emitía antes de que el formato
    /// fuera un dato (decisión 3 del spec).</summary>
    public static DocumentNumberFormat DefaultFor(DocumentNumberType documentType) =>
        new(
            documentType == DocumentNumberType.Order ? "PED-" : "QUO-",
            includeYear: true,
            DefaultYearSeparator,
            DefaultMinDigits);

    private static bool IsAllowedPrefixCharacter(char character) =>
        char.IsAsciiLetterOrDigit(character) || character == '-';
}

/// <summary>
/// Arma el número a partir del formato, el año y el consecutivo. Reemplaza a
/// <c>QuotationNumberFormatter</c> y <c>OrderNumberFormatter</c>, que tenían el formato fijo en
/// código. Es puro: no consulta nada y no comprueba el largo — de los 20 caracteres se encarga el
/// dominio, que ya tiene el código de error.
/// </summary>
internal static class DocumentNumberFormatter
{
    public static string Format(DocumentNumberFormat format, int year, long sequence)
    {
        var digits = sequence.ToString(
            CultureInfo.InvariantCulture.NumberFormat).PadLeft(format.MinDigits, '0');

        return format.IncludeYear
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"{format.Prefix}{year}{format.YearSeparator}{digits}")
            : format.Prefix + digits;
    }
}
