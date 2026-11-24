using System.Globalization;
using BuildingBlocks.Application;
using Modules.Hr.Domain;

namespace Modules.Hr.Application;

/// <summary>
/// La clave y la fecha que llegan por la URL de la consola de valores legales (spec 2026-11-22, «Rutas»). Se leen en el handler y
/// no en el endpoint, después de autorizar: así quien no puede operar recibe 403 y no un 404 o un 422 sobre un dato que todavía no
/// debería ver.
/// </summary>
public static class LegalParameterRoute
{
    private static readonly string[] ParameterNames = Enum.GetNames<LegalParameterKey>();
    private static readonly string[] BracketNames = Enum.GetNames<LegalBracketKey>();

    /// <summary>
    /// El nombre exacto del enum, con sus mayúsculas: <c>smmlv</c> y los números (<c>0</c>, que <c>Enum.TryParse</c> aceptaría) son
    /// 404 <c>key_unknown</c>.
    /// </summary>
    public static LegalParameterKey ParseKey(string raw) =>
        Array.IndexOf(ParameterNames, raw) >= 0
            ? Enum.Parse<LegalParameterKey>(raw)
            : throw UnknownKey(raw);

    public static LegalBracketKey ParseBracketKey(string raw) =>
        Array.IndexOf(BracketNames, raw) >= 0
            ? Enum.Parse<LegalBracketKey>(raw)
            : throw UnknownKey(raw);

    /// <summary>Sólo <c>yyyy-MM-dd</c>; otra forma es 422 <c>date_invalid</c>.</summary>
    public static DateOnly ParseDate(string raw) =>
        DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : throw new HrDomainException(
                HrErrorCodes.LegalParameterDateInvalid,
                "La fecha de vigencia no es válida. Usa el formato aaaa-mm-dd.");

    private static ResourceNotFoundException UnknownKey(string raw) =>
        new(
            HrErrorCodes.LegalParameterKeyUnknown,
            string.Create(CultureInfo.InvariantCulture, $"No existe el valor legal «{Trim(raw)}». Usa el nombre exacto de la clave."));

    // El texto viaja en el mensaje: se acota para que una URL larga no lo infle.
    private static string Trim(string raw) => raw.Length <= 40 ? raw : raw[..40] + "…";
}
