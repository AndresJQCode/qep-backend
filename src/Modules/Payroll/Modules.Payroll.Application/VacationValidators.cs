using System.Globalization;
using FluentValidation;
using Modules.Payroll.Domain;

namespace Modules.Payroll.Application;

/// <summary>
/// La forma del cuerpo de vacaciones. Qué se puede hacer con las fechas (festivos, contrato, primera quincena) lo decide
/// <see cref="VacationRules"/>; acá sólo el campo que falla. Las claves de <c>errors</c> son <c>externalMonths[0].month</c>, etc.
/// </summary>
public sealed class VacationRequestValidator : PayrollValidator<VacationRequest>
{
    public VacationRequestValidator() =>
        RuleFor(request => request).Custom((request, context) =>
        {
            var known = EnumNames.TryParse<VacationOrigin>(request.Origin, out var origin);
            if (!known)
            {
                context.AddFailure("Origin", "Elige un origen válido (Qep o External).");
            }

            VacationDates.Validate(context, request.StartDate, request.EndDate);

            if (request.PreviousMonthIbc is <= 0)
            {
                context.AddFailure("PreviousMonthIbc", "El IBC del mes anterior debe ser mayor que 0.");
            }

            if (!known)
            {
                return;
            }

            if (origin == VacationOrigin.External)
            {
                ValidateExternal(context, request);
            }
            else
            {
                ValidateQep(context, request);
            }
        });

    private static void ValidateQep(FluentValidation.ValidationContext<VacationRequest> context, VacationRequest request)
    {
        if (request.BusinessDays is not null)
        {
            context.AddFailure("BusinessDays", "QEP cuenta los días hábiles: no los envíes.");
        }

        if (request.ExternalMonths is { Count: > 0 })
        {
            context.AddFailure("ExternalMonths", "Sólo las vacaciones externas llevan montos por mes.");
        }
    }

    private static void ValidateExternal(FluentValidation.ValidationContext<VacationRequest> context, VacationRequest request)
    {
        if (request.PreviousMonthIbc is null)
        {
            context.AddFailure("PreviousMonthIbc", "Escribe el IBC del mes anterior al inicio.");
        }

        if (request.BusinessDays is not >= 1)
        {
            context.AddFailure("BusinessDays", "Escribe los días hábiles que pagó Siigo (al menos 1).");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < (request.ExternalMonths?.Count ?? 0); index++)
        {
            var month = request.ExternalMonths![index];
            var path = $"ExternalMonths[{index}]";
            if (month is null)
            {
                context.AddFailure(path, "Falta el mes.");
                continue;
            }

            if (!IsMonthInRange(month.Month, request.StartDate, request.EndDate))
            {
                context.AddFailure($"{path}.Month", "El mes debe tener la forma aaaa-MM y estar dentro de las vacaciones.");
            }
            else if (!seen.Add(month.Month!))
            {
                context.AddFailure($"{path}.Month", "Ese mes ya está en la lista.");
            }

            NotNegative(context, $"{path}.VacationPay", month.VacationPay);
            NotNegative(context, $"{path}.HealthAdvance", month.HealthAdvance);
            NotNegative(context, $"{path}.PensionAdvance", month.PensionAdvance);
            NotNegative(context, $"{path}.SolidarityFundAdvance", month.SolidarityFundAdvance);
        }
    }

    private static void NotNegative(FluentValidation.ValidationContext<VacationRequest> context, string path, decimal? amount)
    {
        if (amount is < 0)
        {
            context.AddFailure(path, "El valor no puede ser negativo.");
        }
    }

    // A month is the first day of «yyyy-MM»; it falls inside when it is between the month of the start and the month of the end.
    private static bool IsMonthInRange(string? text, DateOnly? start, DateOnly? end)
    {
        if (text is null
            || text.Length != 7
            || !DateOnly.TryParseExact(text + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var month))
        {
            return false;
        }

        return start is null || end is null
            || (month >= new DateOnly(start.Value.Year, start.Value.Month, 1) && month <= new DateOnly(end.Value.Year, end.Value.Month, 1));
    }
}

public sealed class VacationPreviewRequestValidator : PayrollValidator<VacationPreviewRequest>
{
    public VacationPreviewRequestValidator() =>
        RuleFor(request => request).Custom((request, context) =>
        {
            if (!EnumNames.TryParse<VacationOrigin>(request.Origin, out _))
            {
                context.AddFailure("Origin", "Elige un origen válido (Qep o External).");
            }

            VacationDates.Validate(context, request.StartDate, request.EndDate);
        });
}

internal static class VacationDates
{
    // Session decision (final review): a vacation spans at most a year, inclusive; it keeps the calendars and the date arithmetic bounded.
    public const int MaxDays = 366;

    public static void Validate<T>(FluentValidation.ValidationContext<T> context, DateOnly? start, DateOnly? end)
    {
        if (start is null)
        {
            context.AddFailure("StartDate", "Escribe el primer día de vacaciones.");
        }

        if (end is null)
        {
            context.AddFailure("EndDate", "Escribe el último día de vacaciones.");
        }
        else if (start is not null && end < start)
        {
            context.AddFailure("EndDate", "El último día no puede ser anterior al primero.");
        }
        else if (start is not null && end.Value.DayNumber - start.Value.DayNumber + 1 > MaxDays)
        {
            context.AddFailure("EndDate", $"Las vacaciones no pueden durar más de {MaxDays} días.");
        }
    }
}
