using Modules.ServiceContracts.Domain;

namespace Modules.ServiceContracts.Application;

/// <summary>El filtro <c>status</c> de <c>GET /charges</c>: <c>open</c> = Pending + Overdue (por vencimiento asc), <c>paid</c> por <c>paidOn</c> desc.</summary>
public enum ChargeListStatus
{
    Open = 1,
    Paid = 2,
    Cancelled = 3,
}

/// <summary>
/// Filtros de <c>GET /services</c> (§4). <c>Status</c> null = todos. <c>SearchTerm</c> busca por nombre del servicio y
/// <c>SearchCustomerIds</c> por los clientes que el puerto de clientes ya resolvió por nombre; basta con que coincida uno de los dos.
/// </summary>
public sealed record ServiceContractQuery(
    Guid TenantId,
    Guid? CustomerId,
    ServiceContractStatus? Status,
    string? SearchTerm,
    IReadOnlyCollection<Guid>? SearchCustomerIds,
    int Page,
    int PageSize);

/// <summary>
/// Filtros de <c>GET /charges</c> (§4). <c>AssigneeMemberId</c> es la membresía que <c>assignee=me</c> ya resolvió: el encargado
/// vive en el perfil del cliente, así que el repositorio la cruza con él.
/// </summary>
public sealed record ChargeQuery(
    Guid TenantId,
    ChargeListStatus Status,
    Guid? CustomerId,
    Guid? AssigneeMemberId,
    string? SearchTerm,
    IReadOnlyCollection<Guid>? SearchCustomerIds,
    int Page,
    int PageSize);
