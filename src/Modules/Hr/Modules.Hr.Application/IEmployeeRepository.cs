using Modules.Hr.Domain;

namespace Modules.Hr.Application;

/// <param name="Active">Los contadores respetan la búsqueda e ignoran estado, vencimiento y empresa.</param>
public sealed record EmployeeListPage(
    IReadOnlyList<Employee> Items,
    int Total,
    int Active,
    int Retired,
    int Deactivated,
    int ExpiringSoon);

public interface IEmployeeRepository
{
    /// <summary>Con contratos, registros y prórrogas, rastreado: lo que se le haga se guarda con el unit of work.</summary>
    Task<Employee?> FindAsync(Guid tenantId, EmployeeId id, CancellationToken cancellationToken);

    void Add(Employee employee);

    void AddRange(IEnumerable<Employee> employees);

    void Remove(Employee employee);

    /// <summary>
    /// <paramref name="companyId"/> filtra por la empresa del contrato <b>vigente</b>: un retirado no tiene empresa en la lista.
    /// <paramref name="expiringSoon"/> es término fijo vigente con fin dentro de 30 días (o ya vencido). Orden: apellido, nombre, id.
    /// </summary>
    Task<EmployeeListPage> ListAsync(
        Guid tenantId,
        string? search,
        EmployeeStatus? status,
        bool expiringSoon,
        Guid? companyId,
        DateOnly today,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    /// <summary>Los números de documento de la lista que ya tiene un empleado del tenant.</summary>
    Task<IReadOnlySet<string>> FindTakenDocumentsAsync(
        Guid tenantId, IReadOnlyCollection<string> documentNumbers, CancellationToken cancellationToken);

    Task<IReadOnlySet<Guid>> ListLinkedMemberIdsAsync(Guid tenantId, CancellationToken cancellationToken);
}
