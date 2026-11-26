using Modules.Payroll.Domain;

namespace Modules.Payroll.Application;

/// <summary>
/// Lo aplicado por cada concepto recurrente de un empleado en las últimas fotos de los períodos anteriores de la empresa (R-12). No
/// hay un contador guardado: reabrir y reliquidar un período viejo recalcula solo.
/// </summary>
public sealed class RecurringAccumulation(IPayrollResultStore results)
{
    /// <param name="before">Sólo los períodos que empiezan antes de esta fecha; nulo = todos los que no son Borrador.</param>
    /// <returns>Por <c>recurringId</c>; el que nunca se aplicó no está (vale 0).</returns>
    public Task<IReadOnlyDictionary<Guid, decimal>> AccumulatedAsync(
        Guid tenantId, Guid companyId, Guid employeeId, DateOnly? before, CancellationToken cancellationToken) =>
        results.SumRecurringAppliedAsync(tenantId, companyId, employeeId, before, cancellationToken);
}

/// <summary>Arma la lista de recurrentes de un empleado con su acumulado y su estado de hoy.</summary>
public sealed class RecurringConceptComposer(
    IRecurringConceptRepository recurring, RecurringAccumulation accumulation, ConceptCatalog catalog)
{
    public async Task<IReadOnlyList<RecurringConceptDto>> ListAsync(
        Guid tenantId, Guid companyId, Guid employeeId, CancellationToken cancellationToken)
    {
        var rows = await recurring.ListByEmployeeAsync(tenantId, companyId, employeeId, cancellationToken);
        if (rows.Count == 0)
        {
            return [];
        }

        var concepts = await catalog.LoadAsync(tenantId, companyId, cancellationToken);
        var accumulated = await accumulation.AccumulatedAsync(tenantId, companyId, employeeId, null, cancellationToken);

        // Activos primero (Active y Exhausted siguen «encendidos»), luego por nombre.
        return rows
            .Select(row => (Row: row, Concept: concepts.Find(row.ConceptId)))
            .OrderByDescending(item => item.Row.Active)
            .ThenBy(item => item.Concept?.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.Row.Id.Value)
            .Select(item => ToDto(item.Row, item.Concept, accumulated.GetValueOrDefault(item.Row.Id.Value)))
            .ToList();
    }

    public static RecurringConceptDto ToDto(RecurringConcept row, ResolvedConcept? concept, decimal accumulated) => new(
        row.Id.Value,
        row.EmployeeId,
        new RecurringConceptRefDto(row.ConceptId, concept?.Name ?? string.Empty, (concept?.Kind ?? ConceptKind.Income).ToString()),
        row.Mode.ToString(),
        row.Amount,
        row.SalaryConstitutive,
        row.Cap,
        accumulated,
        row.Cap is { } cap ? Math.Max(0m, cap - accumulated) : null,
        row.StatusFor(accumulated).ToString(),
        row.Version);
}
