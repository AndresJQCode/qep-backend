namespace Modules.Support.Application;

/// <summary>Un cliente visto desde la bandeja y el detalle: sólo lo que dibujan (nombre y si está activo).</summary>
public sealed record SupportCustomerRef(Guid Id, string Name, bool IsActive);

/// <summary>Un cliente visto desde el alta y la vinculación. <c>Email</c> y <c>Phone</c> son su contacto (<c>Phone</c> es su
/// <c>PhoneE164</c>, que puede ser fijo): el alta los copia en la foto del solicitante cuando éste no los trae, el teléfono en el fijo
/// y nunca en el celular (owner, 2026-11-14 y 2026-11-15, P3). Es un registro aparte de
/// <see cref="SupportCustomerRef"/> para que la bandeja, que resuelve decenas de clientes por página, no lea un contacto que descarta
/// (revisión final, FU3).</summary>
public sealed record SupportCustomerContact(Guid Id, string Name, bool IsActive, string? Email, string? Phone);

/// <summary>Clientes vistos desde Soporte (copia recortada de <c>ILoansCustomerLookup</c>); el adaptador vive en Bootstrapper, sobre
/// <c>CustomersDbContext</c>. Un cliente fusionado está inactivo.</summary>
public interface ISupportCustomerLookup
{
    /// <summary>El tope de <see cref="SearchIdsAsync"/> (revisión final, B2): sus ids viajan como un solo arreglo al filtro de la
    /// bandeja.</summary>
    public const int SearchLimit = 200;

    /// <summary>Un cliente con su contacto: lo usan el alta y la vinculación.</summary>
    Task<SupportCustomerContact?> FindAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken);

    /// <summary>Varios clientes, sin contacto: lo usan la bandeja y el detalle para nombrarlos.</summary>
    Task<IReadOnlyList<SupportCustomerRef>> FindManyAsync(
        Guid tenantId, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken);

    /// <summary>Los ids de los clientes del tenant cuyo nombre contiene <paramref name="term"/>: la búsqueda de la bandeja. Devuelve
    /// como mucho <see cref="SearchLimit"/>, los primeros por nombre y luego id: con un término muy amplio el ticket de algún cliente
    /// puede quedar fuera, y la bandeja igual busca el término en el asunto y en los nombres del solicitante.</summary>
    Task<IReadOnlyCollection<Guid>> SearchIdsAsync(Guid tenantId, string term, CancellationToken cancellationToken);

    /// <summary>El tope de <see cref="FindIdsByDocumentAsync"/>: más que eso ya no es un candidato que la pantalla pueda elegir.</summary>
    public const int DocumentMatchLimit = 10;

    /// <summary>
    /// Fase 3 (plan F21): los clientes activos del tenant con ese tipo y ese número de documento, comparado normalizado (sin signos y en
    /// mayúsculas, la misma regla del índice de Customers), por antigüedad y hasta <see cref="DocumentMatchLimit"/>. Incluye los
    /// incompletos; deja fuera los fusionados y los inactivos, que no se pueden vincular. El tipo va como en el contrato (<c>NIT</c>,
    /// <c>CC</c>…), sin distinguir mayúsculas.
    /// </summary>
    Task<IReadOnlyList<Guid>> FindIdsByDocumentAsync(
        Guid tenantId, string documentType, string documentNumber, CancellationToken cancellationToken);
}
