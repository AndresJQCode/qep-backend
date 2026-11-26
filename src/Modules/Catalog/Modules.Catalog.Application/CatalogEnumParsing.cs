using FluentValidation;
using FluentValidation.Results;

namespace Modules.Catalog.Application;

/// <summary>
/// Lee por nombre un enum que llega como texto en el cuerpo o en la query (spec 2026-11-18: <c>kind</c>,
/// <c>salesUnit</c>, <c>appliesTo</c>). Texto y no el enum en el DTO, por la misma razón que
/// <c>visibleInStore</c>: con el enum, un valor desconocido lo rechaza el binder con 400, y el contrato
/// dice 422 <c>validation.failed</c> con el campo marcado. Sólo el nombre exacto: ni números ni otra
/// capitalización, para que el texto guardado sea siempre el mismo. Público porque lo usan los endpoints
/// (producto) y el mapeo del PUT de tasas, que vive en Application.
/// </summary>
public static class CatalogEnumParsing
{
    public static TEnum? ParseOptional<TEnum>(string? value, string field)
        where TEnum : struct, Enum
    {
        if (value is null)
        {
            return null;
        }

        foreach (var candidate in Enum.GetValues<TEnum>())
        {
            if (string.Equals(candidate.ToString(), value, StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        throw new ValidationException(
            [new ValidationFailure(field, $"{field} tiene que ser uno de: {string.Join(", ", Enum.GetNames<TEnum>())}.")]);
    }
}
