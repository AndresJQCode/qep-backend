using FluentValidation;
using FluentValidation.Results;

namespace Modules.Payroll.Application;

/// <summary>
/// Base de todo validador de Payroll (calco de <c>HrValidator</c>). El contrato de <c>errors</c> usa camelCase y punto para lo
/// anidado, con el índice entre corchetes (<c>overtime[0].hours</c>, <c>incomes[1].amount</c>), y FluentValidation conserva el
/// nombre CLR (<c>Overtime[0].Hours</c>): se reescribe acá, una vez, en vez de repetir <c>OverridePropertyName</c> en cada regla.
/// </summary>
public abstract class PayrollValidator<T> : AbstractValidator<T>
{
    protected override void RaiseValidationException(ValidationContext<T> context, ValidationResult result)
    {
        foreach (var failure in result.Errors)
        {
            failure.PropertyName = ToCamelCasePath(failure.PropertyName);
        }

        base.RaiseValidationException(context, result);
    }

    // Por segmento: «Overtime[0].Hours» pasa a «overtime[0].hours». El índice conserva el corchete porque sólo se baja la primera letra.
    private static string ToCamelCasePath(string path) =>
        string.Join('.', path.Split('.').Select(segment =>
            segment.Length == 0 ? segment : char.ToLowerInvariant(segment[0]) + segment[1..]));
}
