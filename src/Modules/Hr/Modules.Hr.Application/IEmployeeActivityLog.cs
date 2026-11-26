using System.Text.Json;
using Modules.Hr.Domain;

namespace Modules.Hr.Application;

/// <param name="Before">Escalares: montos como número, fechas yyyy-MM-dd, enums por nombre, referencias por el nombre que tenían al escribirse.</param>
/// <param name="Sensitive">Salario, banco, cargo, sede, contrato: el historial enmascarado los quita.</param>
public sealed record ActivityChange(string Field, JsonElement? Before, JsonElement? After, bool Sensitive);

public sealed record EmployeeActivityEntry(
    Guid Id,
    Guid TenantId,
    EmployeeId EmployeeId,
    DateTimeOffset OccurredAt,
    Guid ActorSubjectId,
    string ActorName,
    HrActivityAction Action,
    HrActivityCategory Category,
    Guid? ContractId,
    IReadOnlyList<ActivityChange> Changes);

/// <summary>
/// El historial de acciones del empleado (<c>hr.employee_activity</c>): el antes y el después que <c>audit.entries</c> no
/// guarda. Se escribe en la misma transacción que el cambio.
/// </summary>
public interface IEmployeeActivityLog
{
    void Append(EmployeeActivityEntry entry);

    /// <summary>Más reciente primero. <paramref name="categories"/> es lo que quien llama puede ver.</summary>
    Task<(IReadOnlyList<EmployeeActivityEntry> Items, int Total)> ListAsync(
        Guid tenantId,
        EmployeeId id,
        IReadOnlySet<HrActivityCategory> categories,
        int page,
        int pageSize,
        CancellationToken cancellationToken);
}
