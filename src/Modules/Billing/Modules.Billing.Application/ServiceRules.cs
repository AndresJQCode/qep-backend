using BuildingBlocks.Application;
using FluentValidation;
using Modules.Billing.Domain;
using Modules.Tenancy.Application;

namespace Modules.Billing.Application;

/// <summary>Las reglas de un servicio recurrente (spec cobros-recurrentes §2 y §3.2) que comparten el alta, la edición y el conteo en seco.</summary>
internal static class ServiceRules
{
    /// <summary>
    /// El piso de <c>firstDueDate</c>: <c>BillingCycles.NextDueDate</c> recorre las anclas una a una y el alta genera un cobro por
    /// período hasta hoy, así que una fecha de hace siglos sería un request de miles de cobros. 2000-01-01 cubre cualquier
    /// migración real desde el Excel (un mensual de esa fecha son ~320 períodos).
    /// </summary>
    public static readonly DateOnly EarliestFirstDueDate = new(2000, 1, 1);

    /// <summary>El techo es «hoy + 5 años»: un primer vencimiento más lejano es un error de tipeo (el año 2206), no un contrato.</summary>
    public const int LatestFirstDueDateYearsAhead = 5;

    /// <summary>Precisión de <c>billing.services.price</c> (<c>numeric(18,2)</c>).</summary>
    public const int PricePrecision = 18;

    public const int PriceScale = 2;

    /// <summary>
    /// Sólo los tres nombres exactos del contrato: <c>Enum.TryParse</c> aceptaría «1», «monthly» o un número fuera del enum,
    /// que el cable nunca debe producir.
    /// </summary>
    public static bool TryParseCycle(string? text, out BillingCycle cycle)
    {
        switch (text)
        {
            case nameof(BillingCycle.Monthly):
                cycle = BillingCycle.Monthly;
                return true;
            case nameof(BillingCycle.Semiannual):
                cycle = BillingCycle.Semiannual;
                return true;
            case nameof(BillingCycle.Annual):
                cycle = BillingCycle.Annual;
                return true;
            default:
                cycle = default;
                return false;
        }
    }

    public static IRuleBuilderOptions<T, string?> MustBeABillingCycle<T>(this IRuleBuilder<T, string?> rule) =>
        rule.Must(text => TryParseCycle(text, out _)).WithMessage("Cycle must be Monthly, Semiannual or Annual.");

    public static IRuleBuilderOptions<T, string?> MustBeAServiceName<T>(this IRuleBuilder<T, string?> rule) =>
        rule.Must(text => text?.Trim() is { Length: > 0 and <= BillingService.NameMaxLength })
            .WithMessage($"The name must have between 1 and {BillingService.NameMaxLength} characters.");

    /// <summary>Positivo y dentro de <c>numeric(18,2)</c>: más decimales se redondearían en silencio y más dígitos reventarían el INSERT con un 500.</summary>
    public static IRuleBuilderOptions<T, decimal> MustBeAServicePrice<T>(this IRuleBuilder<T, decimal> rule) =>
        rule.GreaterThan(0m).PrecisionScale(PricePrecision, PriceScale, ignoreTrailingZeros: true);

    /// <summary>
    /// El rango se mide contra la fecha UTC del reloj: un día de diferencia con la «hoy» del tenant no importa a escala de
    /// años, y así el validador no necesita ir a Tenancy.
    /// </summary>
    public static IRuleBuilderOptions<T, DateOnly> MustBeAReasonableFirstDueDate<T>(this IRuleBuilder<T, DateOnly> rule, IClock clock) =>
        rule.Must(date =>
            {
                var latest = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime).AddYears(LatestFirstDueDateYearsAhead);
                return date >= EarliestFirstDueDate && date <= latest;
            })
            .WithMessage($"The first due date must be between 2000-01-01 and {LatestFirstDueDateYearsAhead} years from today.");

    /// <summary>Normaliza el código del catálogo o falla con <c>billing.currency.unknown</c> (422).</summary>
    public static string NormalizeCurrency(string currency)
    {
        if (!Currencies.IsSupported(currency))
        {
            throw new BillingDomainException(BillingErrorCodes.CurrencyUnknown, $"Currency '{currency}' is not supported.");
        }

        return Currencies.Normalize(currency);
    }
}
