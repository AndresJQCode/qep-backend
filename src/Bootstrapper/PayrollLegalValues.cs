using Modules.Hr.Application;
using Modules.Hr.Domain;
using Modules.Payroll.Application;
using Modules.Payroll.Domain;
using Modules.Payroll.Domain.Engine;

namespace Bootstrapper;

/// <summary>Adapta las tablas de valores legales de Hr al puerto de Payroll (<c>PayrollLayerTests.NoPayrollLayerReferencesHr</c>): cada
/// clave se resuelve con la fila vigente a la fecha, y los tramos con los de la mayor vigencia, que también tiene que ser del año.</summary>
internal sealed class PayrollLegalValues(ILegalParameterSource source) : IPayrollLegalValues
{
    // Mid-year keys (or keys with their own start date) keep resolving to the row in force from any earlier year.
    private static bool IsYearly(LegalParameterKey key) =>
        key is not (LegalParameterKey.SundaySurchargeRate or LegalParameterKey.WeeklyHours or LegalParameterKey.ConnectivityAllowanceSince);

    public async Task<LegalValueSet> ResolveAsync(DateOnly asOf, CancellationToken cancellationToken)
    {
        var values = await source.ListAsync(cancellationToken);
        var brackets = await source.ListBracketsAsync(cancellationToken);
        var missing = new List<string>();

        LegalValue Get(LegalParameterKey key)
        {
            var entry = LegalParameters.ResolveEntry(values, key, asOf);

            // Hr no caduca la tabla (D-8), pero la nomina si: una clave sembrada cada 1 de enero que resuelve a una fila de un anio
            // anterior es un anio sin cargar, y la foto congelada del periodo ya no se podria corregir.
            if (entry is null || (IsYearly(key) && entry.EffectiveFrom.Year < asOf.Year))
            {
                missing.Add(key.ToString());
                return new LegalValue(key.ToString(), 0m, asOf);
            }

            return new LegalValue(key.ToString(), entry.Value, entry.EffectiveFrom);
        }

        var solidarity = LegalParameters.ResolveBrackets(brackets, LegalBracketKey.SolidarityFund, asOf);

        // Decisión del owner 6 (2026-11-22): los tramos son anuales para la nómina, como las claves escalares; un conjunto vigente de un
        // año anterior es un año sin cargar.
        if (solidarity.Count == 0 || solidarity[0].EffectiveFrom.Year < asOf.Year)
        {
            missing.Add(LegalBracketKey.SolidarityFund.ToString());
        }

        // El valor de ConnectivityAllowanceSince es 1 y lo que importa es desde cuándo rige (R-1); sin fila, no hay conectividad.
        var connectivity = LegalParameters.ResolveEntry(values, LegalParameterKey.ConnectivityAllowanceSince, asOf);
        var connectivitySince = connectivity is { Value: 1m } ? connectivity.EffectiveFrom : (DateOnly?)null;

        var set = new LegalValueSet(
            asOf,
            Get(LegalParameterKey.Smmlv),
            Get(LegalParameterKey.TransportAllowance),
            Get(LegalParameterKey.HealthEmployeeRate),
            Get(LegalParameterKey.HealthEmployerRate),
            Get(LegalParameterKey.PensionEmployeeRate),
            Get(LegalParameterKey.PensionEmployerRate),
            Get(LegalParameterKey.HighRiskPensionTotalRate),
            Get(LegalParameterKey.ArlClassIRate),
            Get(LegalParameterKey.ArlClassIIRate),
            Get(LegalParameterKey.ArlClassIIIRate),
            Get(LegalParameterKey.ArlClassIVRate),
            Get(LegalParameterKey.ArlClassVRate),
            Get(LegalParameterKey.CompensationFundRate),
            Get(LegalParameterKey.IcbfRate),
            Get(LegalParameterKey.SenaRate),
            Get(LegalParameterKey.SeveranceProvisionRate),
            Get(LegalParameterKey.PremiumProvisionRate),
            Get(LegalParameterKey.VacationProvisionRate),
            Get(LegalParameterKey.SeveranceInterestProvisionRate),
            Get(LegalParameterKey.TransportAllowanceCapSmmlv),
            Get(LegalParameterKey.NonSalaryCapRate),
            Get(LegalParameterKey.ExonerationThresholdSmmlv),
            Get(LegalParameterKey.IbcCapSmmlv),
            Get(LegalParameterKey.SundaySurchargeRate),
            Get(LegalParameterKey.WeeklyHours),
            Get(LegalParameterKey.ExtraDayRate),
            Get(LegalParameterKey.ExtraNightRate),
            Get(LegalParameterKey.NightSurchargeRate),
            connectivitySince,
            solidarity
                .Select(bracket => new SolidarityBracket(bracket.LowerBound, bracket.UpperBound, bracket.Rate, bracket.EffectiveFrom))
                .ToArray());

        if (missing.Count > 0)
        {
            throw new PayrollDomainException(
                PayrollErrorCodes.LegalParameterMissing,
                $"Missing legal values on {asOf:yyyy-MM-dd}: {string.Join(", ", missing)}.");
        }

        return set;
    }
}
