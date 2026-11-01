namespace Modules.ServiceContracts.Domain;

/// <summary>El valor numérico es la cantidad de meses del período.</summary>
public enum BillingCycle
{
    Monthly = 1,
    Semiannual = 6,
    Annual = 12,
}

public static class BillingCycles
{
    /// <summary>
    /// La primera ancla **estrictamente posterior** a <paramref name="after"/>. Las anclas se
    /// calculan siempre desde <paramref name="firstDueDate"/> y no encadenando desde el
    /// vencimiento anterior: <c>AddMonths</c> recorta al último día del mes, así que encadenar
    /// perdería el día para siempre (31 ene → 28 feb → 28 mar). Desde la ancla da 31 mar.
    /// </summary>
    public static DateOnly NextDueDate(DateOnly firstDueDate, BillingCycle cycle, DateOnly after)
    {
        var step = (int)cycle;
        for (var k = 0; ; k++)
        {
            var candidate = firstDueDate.AddMonths(k * step);
            if (candidate > after)
            {
                return candidate;
            }
        }
    }

    /// <summary>La primera ancla <c>&gt;= date</c>.</summary>
    public static DateOnly FirstDueDateOnOrAfter(DateOnly firstDueDate, BillingCycle cycle, DateOnly date) =>
        firstDueDate >= date ? firstDueDate : NextDueDate(firstDueDate, cycle, date.AddDays(-1));

    /// <summary>
    /// Lo que unos precios valen por mes: cada uno dividido por los meses de su ciclo. Se suma primero y se redondea el total, con
    /// la escala de <c>numeric(18,2)</c>: redondear contrato por contrato acumula el error (tres anuales de 100 darían 24.99, no 25.00).
    /// </summary>
    public static decimal MonthlyValue(IEnumerable<(BillingCycle Cycle, decimal Price)> prices) =>
        decimal.Round(prices.Sum(price => price.Price / (int)price.Cycle), 2) + 0.00m;

    public static BillingCycle Parse(string wire) => wire switch
    {
        "Monthly" => BillingCycle.Monthly,
        "Semiannual" => BillingCycle.Semiannual,
        "Annual" => BillingCycle.Annual,
        _ => throw new ServiceContractsDomainException(ServiceContractsErrorCodes.ServiceInvalid, "Unknown billing cycle."),
    };
}
