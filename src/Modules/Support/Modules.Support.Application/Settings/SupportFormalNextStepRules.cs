using FluentValidation;
using Modules.Support.Domain;

namespace Modules.Support.Application;

/// <summary>Fase 4b (H6): la regla del «siguiente paso» que comparten los ajustes del tenant y el alta y edición de un tipo. Cubre la
/// guarda del dominio (<see cref="SupportFormalNextStep.Normalize"/>) para que el largo salga como <c>validation.failed</c> en el campo
/// y no como 500, y además frena los caracteres de control, que el PDF no sabe imprimir.</summary>
internal static class SupportFormalNextStepRules
{
    /// <summary>Null o vacío pasa (es «sin texto»). Se mide recortado, como lo guarda el dominio; el salto de línea y la tabulación
    /// valen.</summary>
    public static IRuleBuilderOptions<T, string?> MustBeAFormalNextStep<T>(this IRuleBuilder<T, string?> rule) =>
        rule.Must(text => text is null || IsValid(text))
            .WithMessage(
                $"The next step has at most {SupportFormalNextStep.MaxLength} characters and no control characters other than line breaks and tabs.");

    private static bool IsValid(string text) =>
        text.Trim().Length <= SupportFormalNextStep.MaxLength &&
        !text.Any(character => char.IsControl(character) && character is not ('\n' or '\r' or '\t'));
}
