using System.Text.Json;
using Modules.Payroll.Domain;
using Modules.Payroll.Domain.Engine;

namespace Modules.Payroll.Application;

/// <summary>
/// De lo guardado (líneas con rastro, foto legal) al contrato HTTP. Las etiquetas y el fundamento de cada valor legal salen de
/// <see cref="LegalBasis"/>, una sola vez.
/// </summary>
public static class PayrollResultMapper
{
    /// <summary>La foto legal completa en el orden del conjunto: cada valor, la fecha de conectividad y un renglón por tramo del FSP.</summary>
    public static IReadOnlyList<LegalValueUseDto> LegalValuesOf(LegalValueSet legal)
    {
        ArgumentNullException.ThrowIfNull(legal);

        LegalValue[] values =
        [
            legal.Smmlv, legal.TransportAllowance, legal.HealthEmployeeRate, legal.HealthEmployerRate, legal.PensionEmployeeRate,
            legal.PensionEmployerRate, legal.HighRiskPensionTotalRate, legal.ArlClassIRate, legal.ArlClassIIRate,
            legal.ArlClassIIIRate, legal.ArlClassIVRate, legal.ArlClassVRate, legal.CompensationFundRate, legal.IcbfRate,
            legal.SenaRate, legal.SeveranceProvisionRate, legal.PremiumProvisionRate, legal.VacationProvisionRate,
            legal.SeveranceInterestProvisionRate, legal.TransportAllowanceCapSmmlv, legal.NonSalaryCapRate,
            legal.ExonerationThresholdSmmlv, legal.IbcCapSmmlv, legal.SundaySurchargeRate, legal.WeeklyHours, legal.ExtraDayRate,
            legal.ExtraNightRate, legal.NightSurchargeRate,
        ];

        var uses = values.Select(Use).ToList();
        if (legal.ConnectivityAllowanceSince is { } since)
        {
            const string key = "ConnectivityAllowanceSince";
            uses.Add(new LegalValueUseDto(key, LegalBasis.LabelOf(key), 1m, since, LegalBasis.BasisOf(key)));
        }

        const string fund = "SolidarityFund";
        foreach (var bracket in legal.SolidarityBrackets.OrderBy(bracket => bracket.LowerSmmlv))
        {
            var range = bracket.UpperSmmlv is { } upper ? $"{bracket.LowerSmmlv:0.##} a {upper:0.##} SMMLV" : $"desde {bracket.LowerSmmlv:0.##} SMMLV";
            uses.Add(new LegalValueUseDto(
                fund, $"{LegalBasis.LabelOf(fund)} ({range})", bracket.Rate, bracket.EffectiveFrom, LegalBasis.BasisOf(fund)));
        }

        return uses;
    }

    public static LegalValueSet? ParseLegal(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<LegalValueSet>(json, PayrollJson.Options);

    /// <summary>
    /// The legal values a period reads: its own photo once it was liquidated, and only before that the ones in force on its start
    /// date. A corrective row inserted later must not change what a liquidated period shows or accepts.
    /// </summary>
    public static async Task<LegalValueSet> LegalOfAsync(
        PayrollPeriod period, IPayrollLegalValues legalValues, CancellationToken cancellationToken) =>
        ParseLegal(period.LegalValuesJson) ?? await legalValues.ResolveAsync(period.StartDate, cancellationToken);

    public static string ToJson(LegalValueSet legal) => JsonSerializer.Serialize(legal, PayrollJson.Options);

    public static PayrollLineDto ToDto(PayrollLine line) => new(
        line.Code,
        line.Group.ToString(),
        line.Label,
        line.Amount,
        new PayrollLineTraceDto(
            line.Trace.Formula,
            line.Trace.Inputs.Select(input => new TraceInputDto(input.Name, input.Value)).ToList(),
            line.Trace.LegalValues.Select(Use).ToList(),
            line.Trace.Note));

    public static EmployeeResultDto ToDto(EmployeeResult result, LegalValueSet? legal) => new(
        result.Liquidation.WorkedDays,
        result.Liquidation.EngineVersion,
        result.Liquidation.Lines.Select(ToDto).ToList(),
        PayrollTotalsDto.From(result.Liquidation.Summary),
        legal is null ? [] : LegalValuesOf(legal));

    private static LegalValueUseDto Use(LegalValue value) =>
        new(value.Key, LegalBasis.LabelOf(value.Key), value.Value, value.EffectiveFrom, LegalBasis.BasisOf(value.Key));
}
