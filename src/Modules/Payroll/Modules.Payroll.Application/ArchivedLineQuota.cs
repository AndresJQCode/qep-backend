using Modules.Payroll.Domain;

namespace Modules.Payroll.Application;

/// <summary>
/// Decisión del owner 5 (2026-11-22): un concepto archivado no entra en líneas nuevas, pero las que el empleado ya tenía guardadas
/// en la quincena se vuelven a guardar, con su valor cambiado o no. La captura reemplaza la lista entera y una línea de la
/// solicitud no trae id, así que se cuenta: por concepto y tipo, a lo sumo tantas como había guardadas.
/// </summary>
public sealed class ArchivedLineQuota
{
    private readonly Dictionary<(Guid ConceptId, ConceptKind Kind), int> _left;

    private ArchivedLineQuota(Dictionary<(Guid ConceptId, ConceptKind Kind), int> left) => _left = left;

    public static ArchivedLineQuota Of(IEnumerable<PeriodInput> saved, Guid employeeId)
    {
        ArgumentNullException.ThrowIfNull(saved);
        return new ArchivedLineQuota(saved
            .Where(input => input.EmployeeId == employeeId)
            .GroupBy(input => (input.ConceptId, input.Kind))
            .ToDictionary(group => group.Key, group => group.Count()));
    }

    /// <summary>Gasta una línea del cupo; falso si no queda: la línea es nueva.</summary>
    public bool TryTake(Guid conceptId, ConceptKind kind)
    {
        var left = _left.GetValueOrDefault((conceptId, kind));
        if (left <= 0)
        {
            return false;
        }

        _left[(conceptId, kind)] = left - 1;
        return true;
    }
}
