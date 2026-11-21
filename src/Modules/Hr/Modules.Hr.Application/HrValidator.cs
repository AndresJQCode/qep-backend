using FluentValidation;
using FluentValidation.Results;

namespace Modules.Hr.Application;

/// <summary>
/// Base de todo validador de Hr. El contrato de <c>errors</c> usa camelCase y punto para lo anidado (<c>firstName</c>,
/// <c>contract.endDate</c>, <c>payment.bankAccountNumber</c>), y FluentValidation conserva el nombre CLR
/// (<c>Contract.EndDate</c>): se reescribe acá, una vez, en vez de repetir <c>OverridePropertyName</c> en cada regla.
/// </summary>
public abstract class HrValidator<T> : AbstractValidator<T>
{
    protected override void RaiseValidationException(ValidationContext<T> context, ValidationResult result)
    {
        foreach (var failure in result.Errors)
        {
            failure.PropertyName = ToCamelCasePath(failure.PropertyName);
        }

        base.RaiseValidationException(context, result);
    }

    // Por segmento: «Contract.Contract.EndDate» pasa a «contract.contract.endDate». Un índice («Items[0].Name») conserva el corchete.
    private static string ToCamelCasePath(string path) =>
        string.Join('.', path.Split('.').Select(segment =>
            segment.Length == 0 ? segment : char.ToLowerInvariant(segment[0]) + segment[1..]));
}
