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

/// <summary><c>Skipped</c>: el cliente sigue incompleto y Customers no le deja tocar la libreta; el pedido entra igual (P6).</summary>
public enum StoreAddressOutcome
{
    Reused,
    Added,
    Skipped,
}

/// <summary>Qué pasó al dar de alta al cliente con el bloque de facturación (P4, P5).</summary>
public enum StoreCustomerCreationOutcome
{
    /// <summary>Esta llamada lo dio de alta, completo.</summary>
    Created,

    /// <summary>El documento ya era de otro cliente activo y sin fusionar: se devuelve ése (puede venir incompleto).</summary>
    ExistingByDocument,

    /// <summary>El documento es de un cliente inactivo o fusionado: no se le cuelga el pedido.</summary>
    DocumentOwnerUnavailable,

    /// <summary>Customers rechazó los datos para una ficha completa: el llamador cae a un incompleto.</summary>
    NotCompletable,
}

/// <param name="Customer"><c>null</c> sólo con <see cref="StoreCustomerCreationOutcome.DocumentOwnerUnavailable"/> y
/// <see cref="StoreCustomerCreationOutcome.NotCompletable"/>.</param>
public sealed record StoreCustomerCreation(StoreCustomerRef? Customer, StoreCustomerCreationOutcome Outcome);

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

    /// <summary>El cliente activo y sin fusionar dueño del documento; <c>null</c> si no hay, si el tipo no es de la lista o si el dueño
    /// está inactivo o fusionado.</summary>
    Task<StoreCustomerRef?> FindByDocumentAsync(
        Guid tenantId, string identificationType, string identificationNumber, CancellationToken cancellationToken);

    /// <summary>
    /// Crea un cliente completo con el bloque de facturación. No lanza por el documento ni por datos que no alcanzan: lo dice
    /// <see cref="StoreCustomerCreation.Outcome"/>. <paramref name="actorUserId"/> <c>null</c> deja el alta como de una integración.
    /// </summary>
    Task<StoreCustomerCreation> CreateAsync(
        Guid tenantId, Guid? actorUserId, StoreNewCustomer data, CancellationToken cancellationToken);

    /// <summary>Un cliente incompleto sin documento: la caída cuando no se puede crear uno completo.</summary>
    Task<StoreCustomerRef> CreateIncompleteAsync(
        Guid tenantId, string name, string? phone, string? email, string? country, CancellationToken cancellationToken);

    /// <summary>
    /// Completa a un incompleto llenando sólo lo que está en blanco (P5). Si Customers no lo puede completar, vuelve como está; si el
    /// documento ya es de otro cliente activo, vuelve ése. Sólo lanza si el cliente dejó de existir o cambió en medio.
    /// </summary>
    Task<StoreCustomerRef> CompleteAsync(
        Guid tenantId, Guid? actorUserId, Guid customerId, StoreNewCustomer billing, CancellationToken cancellationToken);

    /// <summary>Una salida por dirección, en el mismo orden. Idempotente: lo que ya está en la libreta se reutiliza. Un cliente
    /// incompleto devuelve <see cref="StoreAddressOutcome.Skipped"/> para todas.</summary>
    Task<IReadOnlyList<StoreAddressOutcome>> EnsureAddressesAsync(
        Guid tenantId, Guid customerId, IReadOnlyList<StoreAddressInput> addresses, CancellationToken cancellationToken);
}
