namespace Modules.Payroll.Domain.Engine;

/// <summary>
/// La tabla de factores de cada hora (R-2), una sola vez. Una hora extra es una hora adicional y paga <c>1 + recargo</c>; un recargo
/// cae sobre una hora ya pagada y paga sólo el recargo. Los tres compuestos dominicales se derivan del recargo dominical vigente a la
/// fecha del período: no tienen clave propia.
/// </summary>
public static class OvertimeFactors
{
    public static (decimal Factor, IReadOnlyList<LegalValue> Used) For(OvertimeInput input, LegalValueSet legal)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(legal);

        if ((input.SystemKey is null) == (input.CustomMultiplier is null))
        {
            throw new InvalidOperationException("An overtime capture carries exactly one of a system key or a custom multiplier.");
        }

        if (input.CustomMultiplier is { } multiplier)
        {
            return (multiplier, []);
        }

        return input.SystemKey!.Value switch
        {
            SystemConceptKey.ExtraDay => (1m + legal.ExtraDayRate.Value, [legal.ExtraDayRate]),
            SystemConceptKey.ExtraNight => (1m + legal.ExtraNightRate.Value, [legal.ExtraNightRate]),
            SystemConceptKey.NightSurcharge => (legal.NightSurchargeRate.Value, [legal.NightSurchargeRate]),
            SystemConceptKey.SundaySurcharge => (legal.SundaySurchargeRate.Value, [legal.SundaySurchargeRate]),
            SystemConceptKey.ExtraSundayDay => (
                1m + legal.ExtraDayRate.Value + legal.SundaySurchargeRate.Value,
                [legal.ExtraDayRate, legal.SundaySurchargeRate]),
            SystemConceptKey.ExtraSundayNight => (
                1m + legal.ExtraNightRate.Value + legal.SundaySurchargeRate.Value,
                [legal.ExtraNightRate, legal.SundaySurchargeRate]),
            SystemConceptKey.NightSundaySurcharge => (
                legal.NightSurchargeRate.Value + legal.SundaySurchargeRate.Value,
                [legal.NightSurchargeRate, legal.SundaySurchargeRate]),
            _ => throw new InvalidOperationException($"{input.SystemKey} is not an overtime concept."),
        };
    }
}
