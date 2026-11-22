namespace Modules.Payroll.Domain.Engine;

public sealed record TransportOutcome(PayrollLine Line, decimal Raw);

/// <summary>Paso 6: auxilio de transporte, o de conectividad para el teletrabajador desde que la ley lo dio (R-23).</summary>
public static class TransportAllowanceRule
{
    private const string Formula =
        "redondeo al peso(auxilio ÷ 30 × días trabajados, acumulado del mes) − lo pagado en la primera quincena; 0 si el ingreso del mes supera el tope";

    public static TransportOutcome Compute(EngineInput input, WorkedDays worked, SalaryOutcome salary, MonthIncome month)
    {
        var legal = input.Legal;
        var connectivity = salary.Teleworker
            && legal.ConnectivityAllowanceSince is { } since
            && salary.SalaryRecordDate >= since;
        var code = connectivity ? "connectivity_allowance" : "transport_allowance";
        var label = connectivity ? "Auxilio de conectividad" : "Auxilio de transporte";

        var cap = legal.TransportAllowanceCapSmmlv.Value * legal.Smmlv.Value;
        var legalValues = new List<LegalValue> { legal.TransportAllowance, legal.TransportAllowanceCapSmmlv, legal.Smmlv };
        var inputs = new List<TraceInput>
        {
            new("Días trabajados", worked.Days),
            new("Ingreso para el tope", month.TransportCapIncome),
            new("Tope", cap),
        };

        PayrollLine Line(decimal amount, string? note) =>
            new(code, PayrollLineGroup.Earning, label, amount, null, new LineTrace(Formula, inputs, legalValues, note));

        if (month.TransportCapIncome > cap)
        {
            return new TransportOutcome(Line(0m, $"El ingreso del mes supera {legal.TransportAllowanceCapSmmlv.Value:0.##} SMMLV"), 0m);
        }

        var allowance = legal.TransportAllowance.Value;
        var raw = allowance * worked.Days / 30m;
        if (input.Settings.TransportFrequency == TransportFrequency.Monthly)
        {
            if (input.Period.Half == PayrollHalf.First)
            {
                return new TransportOutcome(Line(0m, "Se paga en la segunda quincena"), 0m);
            }

            var wholeMonth = PayrollRounding.ToPeso(allowance * (input.Prior.WorkedDays + worked.Days) / 30m);
            return new TransportOutcome(Line(wholeMonth, null), raw);
        }

        var amount = PayrollRounding.ToPeso(input.Prior.TransportRaw + raw) - input.Prior.Transport;
        return new TransportOutcome(Line(amount, null), raw);
    }
}
