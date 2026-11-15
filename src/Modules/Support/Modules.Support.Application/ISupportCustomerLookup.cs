namespace Modules.Support.Application;

/// <summary>Un cliente visto desde Soporte. <c>Email</c> y <c>MobilePhone</c> son el contacto del cliente (su teléfono ya en E.164):
/// el alta manual los copia en la foto del solicitante cuando éste no los trae (owner, 2026-11-14).</summary>
public sealed record SupportCustomerRef(Guid Id, string Name, bool IsActive, string? Email, string? MobilePhone);

/// <summary>Clientes vistos desde Soporte (copia recortada de <c>ILoansCustomerLookup</c>); el adaptador vive en Bootstrapper, sobre
/// <c>CustomersDbContext</c>. Un cliente fusionado está inactivo.</summary>
public interface ISupportCustomerLookup
{
    /// <summary>El tope de <see cref="SearchIdsAsync"/> (revisión final, B2): sus ids viajan como un solo arreglo al filtro de la
    /// bandeja.</summary>
    public const int SearchLimit = 200;

    Task<SupportCustomerRef?> FindAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken);

    Task<IReadOnlyList<SupportCustomerRef>> FindManyAsync(
        Guid tenantId, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken);

    /// <summary>Los ids de los clientes del tenant cuyo nombre contiene <paramref name="term"/>: la búsqueda de la bandeja. Devuelve
    /// como mucho <see cref="SearchLimit"/>, los primeros por nombre y luego id: con un término muy amplio el ticket de algún cliente
    /// puede quedar fuera, y la bandeja igual busca el término en el asunto y en los nombres del solicitante.</summary>
    Task<IReadOnlyCollection<Guid>> SearchIdsAsync(Guid tenantId, string term, CancellationToken cancellationToken);
}
