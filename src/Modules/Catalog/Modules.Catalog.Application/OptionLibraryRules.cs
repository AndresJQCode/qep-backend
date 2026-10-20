using FluentValidation;
using FluentValidation.Results;
using Modules.Catalog.Domain;

namespace Modules.Catalog.Application;

/// <summary>
/// Las reglas de texto libre de la biblioteca. El dominio hace cumplir las mismas y tiraría un 422
/// con un solo código; el validador existe para que la respuesta lleve el mapa <c>errors</c> por
/// campo, que es lo que el formulario sabe leer. Cada regla lleva de código de error el mismo
/// código del dominio, y el largo se mide como lo mide el dominio: después de recortar los bordes.
/// </summary>
internal static class OptionLibraryRules
{
    public static void Name<T>(IRuleBuilderInitial<T, string> rule) =>
        rule.Cascade(CascadeMode.Stop)
            .NotEmpty()
            .WithErrorCode("catalog.option.name_required")
            .WithMessage("The option name is required.")
            .Must(name => name.Trim().Length <= OptionDefinition.NameMaxLength)
            .WithErrorCode("catalog.option.name_too_long")
            .WithMessage($"The option name cannot exceed {OptionDefinition.NameMaxLength} characters.");

    /// <summary>
    /// Revisa cada valor en orden y marca el campo <c>{prefix}[i]{suffix}</c>. El repetido es el
    /// segundo, no el primero: es el que el usuario acaba de escribir.
    /// </summary>
    public static void Values<T>(
        IReadOnlyList<string?> values,
        string prefix,
        string suffix,
        ValidationContext<T> context)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < values.Count; index++)
        {
            var field = $"{prefix}[{index}]{suffix}";
            var value = values[index];
            if (string.IsNullOrWhiteSpace(value))
            {
                context.AddFailure(Failure(field, "catalog.option.value_required", "The option value is required."));
                continue;
            }

            var trimmed = value.Trim();
            if (trimmed.Length > OptionDefinition.ValueMaxLength)
            {
                context.AddFailure(Failure(
                    field,
                    "catalog.option.value_too_long",
                    $"The option value cannot exceed {OptionDefinition.ValueMaxLength} characters."));
            }
            else if (!seen.Add(trimmed))
            {
                context.AddFailure(Failure(field, "catalog.option.value_duplicate", "The option already has that value."));
            }
        }
    }

    private static ValidationFailure Failure(string field, string code, string message) =>
        new(field, message) { ErrorCode = code };
}
