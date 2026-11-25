namespace Modules.Payroll.Domain.Engine;

public sealed record TransportOutcome(PayrollLine Line, decimal Raw);

/// <summary>Paso 6: auxilio de transporte, o de conectividad para el teletrabajador desde que la ley lo dio (R-23).</summary>
public static class TransportAllowanceRule
{
    private const string Formula =
        "redondeo al peso(auxilio ÷ 30 × días trabajados, acumulado del mes) − lo pagado en la primera quincena; 0 si el ingreso del mes supera el tope, sin descontar lo pagado";

    public static TransportOutcome Compute(EngineInput input, WorkedDays worked, SalaryOutcome salary, MonthIncome month) =>
        Compute(input, worked, salary, month, VacationDaysOutcome.None);

    public static TransportOutcome Compute(
        EngineInput input,
        WorkedDays worked,
        SalaryOutcome salary,
        MonthIncome month,
        VacationDaysOutcome vacations)
    {
        // No vacation day accrues the allowance (Ley 15/1959), paid in advance or not: only the days worked count.
        var workedDays = worked.Days - vacations.Days;
        var legal = input.Legal;
        var connectivity = salary.Teleworker
            && legal.ConnectivityAllowanceSince is { } since
            && salary.SalaryRecordDate >= since;
        var code = connectivity ? "connectivity_allowance" : "transport_allowance";
        var label = connectivity ? "Auxilio de conectividad" : "Auxilio de transporte";

        var cap = legal.TransportAllowanceCapSmmlv.Value * legal.Smmlv.Value;
        var legalValues = new List<LegalValue> { legal.TransportAllowance, legal.TransportAllowanceCapSmmlv, legal.Smmlv };
        var paidBefore = input.Prior.Transport;
        var inputs = new List<TraceInput>
        {
            new("Días trabajados", workedDays),
            new("Ingreso para el tope", month.TransportCapIncome),
            new("Tope", cap),
            new("Pagado en la primera quincena", paidBefore),
        };

        PayrollLine Line(decimal amount, string? note) =>
            new(code, PayrollLineGroup.Earning, label, amount, null, new LineTrace(Formula, inputs, legalValues, note));

        // The raw accrues by days whether or not the month qualifies (decision 2): the second half settles the whole month, so a
        // first half over the cap still leaves its days for a month that ends under it.
        var raw = legal.TransportAllowance.Value * workedDays / 30m;

        if (month.TransportCapIncome > cap)
        {
            var over = $"El ingreso del mes supera {legal.TransportAllowanceCapSmmlv.Value:0.##} SMMLV";
            // CST art. 149: what the first half paid is never deducted; the line stays at 0 and says so.
            return new TransportOutcome(
                Line(0m, paidBefore > 0m ? $"{over}: lo pagado en la primera quincena no se descuenta (CST art. 149)" : over), raw);
        }

        // Monthly pays in the second half but accumulates the first half's raw too, so a mid-month switch of the frequency neither
        // pays the first half twice nor drops it: both frequencies settle as round(accumulated raw) - what was already paid.
        // Monthly pays in the last half worked: the second, or the first when the contract ends inside it (decision 3).
        if (input.Settings.TransportFrequency == TransportFrequency.Monthly && input.Period.Half == PayrollHalf.First)
        {
            if (!worked.FinalSettlementPending)
            {
                return new TransportOutcome(Line(0m, "Se paga en la segunda quincena"), raw);
            }

            var settled = Math.Max(0m, PayrollRounding.ToPeso(input.Prior.TransportRaw + raw) - paidBefore);
            return new TransportOutcome(Line(settled, "El contrato termina en esta quincena: se paga lo causado del mes"), raw);
        }

        var amount = Math.Max(0m, PayrollRounding.ToPeso(input.Prior.TransportRaw + raw) - paidBefore);
        return new TransportOutcome(Line(amount, null), raw);
    }
}
