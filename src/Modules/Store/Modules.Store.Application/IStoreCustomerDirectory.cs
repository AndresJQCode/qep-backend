namespace Modules.Store.Application;

/// <summary>Un cliente del tenant visto desde la tienda. <c>WithRetention</c> y <c>VatSurplus</c> son el perfil tributario que «Tomar» le pone a la parte de facturación del pedido.</summary>
public sealed record StoreCustomerRef(Guid Id, bool IsComplete, bool WithRetention, bool VatSurplus);

/// <param name="MatchedBy"><c>Phone</c> y/o <c>Document</c>.</param>
public sealed record StoreCustomerCandidate(
    Guid Id,
    string Name,
    string? PhoneE164,
    string? IdentificationType,
    string? IdentificationNumber,
    bool IsComplete,
    IReadOnlyList<string> MatchedBy);

/// <summary>El bloque de facturación de la solicitud, tal como «Tomar» lo usa para crear o completar al cliente.</summary>
public sealed record StoreNewCustomer(
    string Name,
    string IdentificationType,
    string IdentificationNumber,
    string Phone,
    string Email,
    string Address,
    string Country,
    Guid? CityId,
    string? CityName,
    Guid? ClassificationId);

/// <summary>Una dirección de la solicitud para la libreta del cliente. <c>Phone</c> va en E.164; en <c>CO</c> sólo <c>CityId</c>, afuera sólo <c>CityName</c>.</summary>
public sealed record StoreAddressInput(string Name, string Address, string? Phone, string Country, Guid? CityId, string? CityName);

public enum StoreAddressOutcome
{
    Reused,
    Added,
}

/// <summary>
/// Clientes vistos desde la tienda. El adaptador vive en Bootstrapper sobre Customers; Store no lo referencia. Todo está acotado al
/// <c>tenantId</c> que llega.
/// </summary>
public interface IStoreCustomerDirectory
{
    /// <summary>Los clientes activos y sin fusionar que coinciden por teléfono o por documento (a lo sumo uno por documento).</summary>
    Task<IReadOnlyList<StoreCustomerCandidate>> FindCandidatesAsync(
        Guid tenantId, string phoneE164, string? identificationType, string? identificationNumber, CancellationToken cancellationToken);

    /// <summary>Los índices de <paramref name="keys"/> cuyo teléfono o documento ya es de un cliente: una consulta por clase para toda una página.</summary>
    Task<IReadOnlySet<int>> KnownAsync(
        Guid tenantId,
        IReadOnlyCollection<(string PhoneE164, string? Type, string? Number)> keys,
        CancellationToken cancellationToken);

    /// <summary><c>null</c> si no existe, es de otro tenant, está inactivo o fusionado.</summary>
    Task<StoreCustomerRef?> FindEligibleAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken);

    /// <summary>
    /// Crea un cliente completo con el bloque de facturación. Si el documento ya era de otro cliente devuelve ése con
    /// <c>Created = false</c> (puede venir incompleto: el llamador lo completa).
    /// </summary>
    Task<(StoreCustomerRef Customer, bool Created)> CreateAsync(
        Guid tenantId, Guid actorUserId, StoreNewCustomer data, CancellationToken cancellationToken);

    /// <summary>Completa a un incompleto llenando sólo lo que está en blanco. Un cliente que no se puede tomar es 422 <c>customer_not_found</c>; un documento ajeno, 409 <c>customer_document_taken</c>.</summary>
    Task<StoreCustomerRef> CompleteAsync(
        Guid tenantId, Guid actorUserId, Guid customerId, StoreNewCustomer billing, CancellationToken cancellationToken);

    /// <summary>Una salida por dirección, en el mismo orden. Idempotente: lo que ya está en la libreta se reutiliza.</summary>
    Task<IReadOnlyList<StoreAddressOutcome>> EnsureAddressesAsync(
        Guid tenantId, Guid customerId, IReadOnlyList<StoreAddressInput> addresses, CancellationToken cancellationToken);
}
