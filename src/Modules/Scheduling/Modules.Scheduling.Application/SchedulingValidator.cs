using FluentValidation;
using FluentValidation.Results;

namespace Modules.Scheduling.Application;

/// <summary>
/// Base de todo validador de la Agenda. El contrato de <c>errors</c> usa camelCase y punto para lo anidado (<c>name</c>,
/// <c>weekly[0].ranges</c>), y FluentValidation conserva el nombre CLR: se reescribe acá, una vez, en vez de repetir
/// <c>OverridePropertyName</c> en cada regla. Calco de <c>HrValidator</c>.
/// </summary>
public abstract class SchedulingValidator<T> : AbstractValidator<T>
{
    protected override void RaiseValidationException(ValidationContext<T> context, ValidationResult result)
    {
        foreach (var failure in result.Errors)
        {
            failure.PropertyName = ToCamelCasePath(failure.PropertyName);
        }

        base.RaiseValidationException(context, result);
    }

    // Por segmento: «Weekly[0].Ranges» pasa a «weekly[0].ranges». Un índice conserva el corchete.
    private static string ToCamelCasePath(string path) =>
        string.Join('.', path.Split('.').Select(segment =>
            segment.Length == 0 ? segment : char.ToLowerInvariant(segment[0]) + segment[1..]));
}
