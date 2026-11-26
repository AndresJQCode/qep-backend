using FluentValidation;
using Modules.Payroll.Domain;

namespace Modules.Payroll.Application;

/// <summary>Los límites de una captura: los mismos de la importación masiva (pre-flight M-13).</summary>
public static class InputLimits
{
    public const int MaxOvertimeLines = 20;

    public const int MaxIncomeLines = 50;

    public const int MaxDeductionLines = 50;

    public const decimal MaxHours = 360m;

    /// <summary>Exclusivo: un monto es menor que 10 000 000 000 000 000.</summary>
    public const decimal MoneyCeiling = 10_000_000_000_000_000m;

    public static bool HasAtMostTwoDecimals(decimal value) => decimal.Round(value, 2) == value;

    public static bool IsValidAmount(decimal? value) =>
        value is { } amount && amount > 0m && amount < MoneyCeiling && HasAtMostTwoDecimals(amount);

    public static bool IsValidHours(decimal? value) =>
        value is { } hours && hours > 0m && hours <= MaxHours && HasAtMostTwoDecimals(hours);
}

/// <summary>
/// Un solo validador de raíz para las tres listas: las claves son <c>overtime[0].hours</c> o <c>incomes[1].amount</c>, y el concepto
/// repetido de las horas se señala en la <b>segunda</b> aparición. Que el concepto exista, esté vivo y sea de su tipo lo decide el
/// caso de uso contra el catálogo.
/// </summary>
public sealed class EmployeeInputsRequestValidator : PayrollValidator<EmployeeInputsRequest>
{
    public EmployeeInputsRequestValidator() =>
        RuleFor(request => request).Custom((request, context) =>
        {
            AddListFailure(context, "Overtime", request.Overtime, InputLimits.MaxOvertimeLines);
            AddListFailure(context, "Incomes", request.Incomes, InputLimits.MaxIncomeLines);
            AddListFailure(context, "Deductions", request.Deductions, InputLimits.MaxDeductionLines);

            var seen = new HashSet<Guid>();
            for (var index = 0; index < (request.Overtime?.Count ?? 0); index++)
            {
                var line = request.Overtime![index];
                if (line is null)
                {
                    context.AddFailure($"Overtime[{index}]", "Falta la línea.");
                    continue;
                }

                ConceptRequired(context, $"Overtime[{index}]", line.ConceptId);
                if (line.ConceptId is { } conceptId && conceptId != Guid.Empty && !seen.Add(conceptId))
                {
                    context.AddFailure($"Overtime[{index}].ConceptId", "Ese concepto ya está en la lista: suma sus horas en una sola línea.");
                }

                if (!InputLimits.IsValidHours(line.Hours))
                {
                    context.AddFailure(
                        $"Overtime[{index}].Hours", "Las horas deben ser mayores que 0, hasta 360 y con máximo 2 decimales.");
                }
            }

            for (var index = 0; index < (request.Incomes?.Count ?? 0); index++)
            {
                var line = request.Incomes![index];
                if (line is null)
                {
                    context.AddFailure($"Incomes[{index}]", "Falta la línea.");
                    continue;
                }

                ConceptRequired(context, $"Incomes[{index}]", line.ConceptId);
                AmountRule(context, $"Incomes[{index}]", line.Amount);
                if (line.SalaryConstitutive is null)
                {
                    context.AddFailure($"Incomes[{index}].SalaryConstitutive", "Indica si el ingreso constituye salario.");
                }
            }

            for (var index = 0; index < (request.Deductions?.Count ?? 0); index++)
            {
                var line = request.Deductions![index];
                if (line is null)
                {
                    context.AddFailure($"Deductions[{index}]", "Falta la línea.");
                    continue;
                }

                ConceptRequired(context, $"Deductions[{index}]", line.ConceptId);
                AmountRule(context, $"Deductions[{index}]", line.Amount);
            }
        });

    private static void AddListFailure<T>(ValidationContext<EmployeeInputsRequest> context, string name, IReadOnlyList<T>? list, int max)
    {
        if (list is null)
        {
            context.AddFailure(name, "Falta la lista (puede ir vacía).");
        }
        else if (list.Count > max)
        {
            context.AddFailure(name, $"Máximo {max} líneas por empleado y quincena.");
        }
    }

    private static void ConceptRequired(ValidationContext<EmployeeInputsRequest> context, string line, Guid? conceptId)
    {
        if (conceptId is null || conceptId == Guid.Empty)
        {
            context.AddFailure($"{line}.ConceptId", "Elige un concepto.");
        }
    }

    private static void AmountRule(ValidationContext<EmployeeInputsRequest> context, string line, decimal? amount)
    {
        if (!InputLimits.IsValidAmount(amount))
        {
            context.AddFailure($"{line}.Amount", "El valor debe ser mayor que 0 y con máximo 2 decimales.");
        }
    }
}

public sealed class RecurringConceptRequestValidator : PayrollValidator<RecurringConceptRequest>
{
    public RecurringConceptRequestValidator()
    {
        RuleFor(request => request.ConceptId)
            .Must(value => value is { } id && id != Guid.Empty).WithMessage("Elige un concepto.");
        RuleFor(request => request.Mode)
            .Must(value => EnumNames.TryParse<RecurringMode>(value, out _)).WithMessage("Elige un modo válido (Fixed o Proportional).");
        RuleFor(request => request.Amount)
            .Must(InputLimits.IsValidAmount).WithMessage("El valor debe ser mayor que 0 y con máximo 2 decimales.");
        RuleFor(request => request.Cap)
            .Must(value => InputLimits.IsValidAmount(value)).WithMessage("El tope debe ser mayor que 0 y con máximo 2 decimales.")
            .When(request => request.Cap is not null);
        RuleFor(request => request.Cap)
            .Must((request, cap) => cap is not { } value || request.Amount is not { } amount || value >= amount)
            .WithMessage("El tope no puede ser menor que el valor.")
            .When(request => request.Cap is not null && InputLimits.IsValidAmount(request.Cap));
    }
}
