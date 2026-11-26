namespace Modules.Payroll.Domain;

/// <summary>El estado de un período de nómina (máquina de estados del contrato HTTP).</summary>
public enum PayrollPeriodStatus
{
    Draft,
    Liquidated,
    Approved,
    Closed,
    Reopened,
}

/// <summary>Lo que el historial del período registra (<c>payroll.period_events</c>).</summary>
public enum PeriodAction
{
    Created,
    InputsImported,
    Liquidated,
    Approved,
    Closed,
    Reopened,
    MarkedOutdated,
}

/// <summary>El estado de un concepto recurrente. <c>Exhausted</c> se calcula al leer, no se guarda (R-12).</summary>
public enum RecurringStatus
{
    Active,
    Inactive,
    Exhausted,
}
