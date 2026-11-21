using Modules.Hr.Domain;

namespace Modules.Hr.Application;

/// <summary>Normalización de texto de Hr: un solo lugar para que la configuración y la importación comparen igual.</summary>
public static class HrText
{
    /// <summary>
    /// Sin mayúsculas, tildes ni espacios de más: la comparación de etiquetas (importación) y la unicidad de nombres de la
    /// configuración usan la misma forma (<see cref="OrganizationNames.Normalize"/>).
    /// </summary>
    public static string? Fold(string? text) => string.IsNullOrWhiteSpace(text) ? null : OrganizationNames.Normalize(text);

    /// <summary>Recorta, y un texto vacío es «sin valor»: nunca se guarda una cadena vacía.</summary>
    public static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
