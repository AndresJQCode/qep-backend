namespace Modules.Scheduling.Application;

/// <summary>El cliente de una cita tal como la Agenda lo necesita: nombre y teléfono, y si todavía se puede agendar.</summary>
public sealed record SchedulingCustomerRef(Guid Id, string Name, string? Phone, bool IsActive);

public sealed record NewSchedulingCustomer(string Name, string? Phone);

/// <summary>
/// Puerto hacia Clientes (plan 2026-11-21, corrección 5): los roles de la Agenda no llevan permisos de Clientes, así que buscar y dar de
/// alta al cliente de la cita pasa por acá, con el permiso <c>appointment.manage</c> de la Agenda, como la caja del POS. El adaptador vive
/// en Bootstrapper y toda lectura va acotada al tenant: un cliente de otro tenant no aparece.
/// </summary>
public interface ISchedulingCustomerLookup
{
    /// <summary>Un solo término contra nombre, documento y teléfono; sin término, los más nuevos. Sólo clientes activos.</summary>
    Task<IReadOnlyList<SchedulingCustomerRef>> SearchAsync(Guid tenantId, string? search, int take, CancellationToken cancellationToken);

    /// <summary>El cliente del tenant, activo o no; <c>null</c> si no existe en él.</summary>
    Task<SchedulingCustomerRef?> FindAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken);

    /// <summary>El nombre de cada uno de esos clientes en una sola consulta, para dibujar una grilla de citas sin pedir un cliente por fila. Un
    /// id que no existe en el tenant no aparece en el resultado.</summary>
    Task<IReadOnlyDictionary<Guid, string>> FindNamesAsync(
        Guid tenantId, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken);

    /// <summary>Una ficha incompleta con nombre y, si lo dio, teléfono: el cliente puede completarse después en Clientes.</summary>
    Task<SchedulingCustomerRef> CreateAsync(Guid tenantId, NewSchedulingCustomer input, CancellationToken cancellationToken);
}
