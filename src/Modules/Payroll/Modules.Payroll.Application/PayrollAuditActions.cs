namespace Modules.Payroll.Application;

/// <summary>
/// Acciones de <c>audit.entries</c> de Payroll. <c>audit.entries</c> guarda <b>nombres</b> de campos (camelCase), nunca valores:
/// las transiciones del período viven además en <c>payroll.period_events</c>. Las acciones se agregan con cada caso de uso.
/// </summary>
public static class PayrollAuditActions
{
    public const string PeriodCreated = "payroll.period.created";
    public const string SettingsUpdated = "payroll.settings.updated";
    public const string ConceptCreated = "payroll.concept.created";
    public const string ConceptUpdated = "payroll.concept.updated";
    public const string ConceptArchived = "payroll.concept.archived";
    public const string ConceptUnarchived = "payroll.concept.unarchived";
    public const string InputsUpdated = "payroll.period.inputs_updated";
    public const string PeriodLiquidated = "payroll.period.liquidated";
    public const string PeriodApproved = "payroll.period.approved";
    public const string PeriodClosed = "payroll.period.closed";
    public const string PeriodReopened = "payroll.period.reopened";
    public const string RecurringCreated = "payroll.recurring.created";
    public const string RecurringUpdated = "payroll.recurring.updated";
    public const string RecurringActivated = "payroll.recurring.activated";
    public const string RecurringDeactivated = "payroll.recurring.deactivated";

    public static class Resources
    {
        public const string Period = "period";
        public const string Settings = "settings";
        public const string Concept = "concept";
        public const string Recurring = "recurring";
    }
}
