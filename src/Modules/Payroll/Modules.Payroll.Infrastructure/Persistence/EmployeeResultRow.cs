using System.Text.Json;
using Modules.Payroll.Application;
using Modules.Payroll.Domain;
using Modules.Payroll.Domain.Engine;

namespace Modules.Payroll.Infrastructure.Persistence;

/// <summary>
/// El resultado de un empleado en un período, como se guarda (<c>payroll.employee_results</c>): sus datos de encabezado en columnas y el
/// resto, líneas con su rastro, resumen, acumulados del mes y lo aplicado por cada recurrente, en <c>jsonb</c>. Una fila se
/// actualiza en su lugar al volver a liquidar.
/// </summary>
internal sealed class EmployeeResultRow
{
    public Guid Id { get; set; }

    public PayrollPeriodId PeriodId { get; set; }

    public Guid EmployeeId { get; set; }

    public string DocumentType { get; set; } = string.Empty;

    public string DocumentNumber { get; set; } = string.Empty;

    public string FullName { get; set; } = string.Empty;

    public string? Position { get; set; }

    public string? Workplace { get; set; }

    public int WorkedDays { get; set; }

    public bool NegativeNet { get; set; }

    public bool FinalSettlementPending { get; set; }

    public string EngineVersion { get; set; } = string.Empty;

    public string Fingerprint { get; set; } = string.Empty;

    public string LinesJson { get; set; } = "[]";

    public string SummaryJson { get; set; } = "{}";

    public string MonthJson { get; set; } = "{}";

    public string RecurringAppliedJson { get; set; } = "{}";

    public int VacationDays { get; set; }

    /// <summary>What the paying quincena froze for the following ones; <c>[]</c> when the result has no vacation to photograph.</summary>
    public string VacationPhotosJson { get; set; } = "[]";

    public static EmployeeResultRow From(PayrollPeriodId periodId, EmployeeResult result)
    {
        var row = new EmployeeResultRow { Id = Guid.CreateVersion7(), PeriodId = periodId };
        row.Apply(result);
        return row;
    }

    public void Apply(EmployeeResult result)
    {
        var liquidation = result.Liquidation;
        EmployeeId = result.EmployeeId;
        DocumentType = result.DocumentType;
        DocumentNumber = result.DocumentNumber;
        FullName = result.FullName;
        Position = result.Position;
        Workplace = result.Workplace;
        WorkedDays = liquidation.WorkedDays;
        NegativeNet = liquidation.NegativeNet;
        FinalSettlementPending = liquidation.FinalSettlementPending;
        EngineVersion = liquidation.EngineVersion;
        Fingerprint = result.Fingerprint;
        LinesJson = JsonSerializer.Serialize(liquidation.Lines, PayrollJson.Options);
        SummaryJson = JsonSerializer.Serialize(liquidation.Summary, PayrollJson.Options);
        MonthJson = JsonSerializer.Serialize(liquidation.Month, PayrollJson.Options);
        RecurringAppliedJson = JsonSerializer.Serialize(result.RecurringApplied, PayrollJson.Options);
        VacationDays = liquidation.VacationDays;
        VacationPhotosJson = JsonSerializer.Serialize(liquidation.VacationPhotos, PayrollJson.Options);
    }

    public EmployeeResult ToResult() => new(
        EmployeeId,
        DocumentType,
        DocumentNumber,
        FullName,
        Position,
        Workplace,
        new EmployeeLiquidation(
            EmployeeId,
            WorkedDays,
            FinalSettlementPending,
            NegativeNet,
            JsonSerializer.Deserialize<List<PayrollLine>>(LinesJson, PayrollJson.Options)!,
            JsonSerializer.Deserialize<PayrollSummary>(SummaryJson, PayrollJson.Options)!,
            JsonSerializer.Deserialize<MonthAccumulators>(MonthJson, PayrollJson.Options)!,
            EngineVersion,
            VacationDays,
            JsonSerializer.Deserialize<List<VacationPhoto>>(VacationPhotosJson, PayrollJson.Options)!),
        Fingerprint,
        ParseApplied(RecurringAppliedJson));

    public static IReadOnlyDictionary<Guid, decimal> ParseApplied(string json) =>
        JsonSerializer.Deserialize<Dictionary<Guid, decimal>>(json, PayrollJson.Options) ?? [];
}
