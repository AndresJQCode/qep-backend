using Modules.Geography.Domain;

namespace Modules.Geography.Application;

/// <summary>Un pedido de un tenant. Las llaves van siempre en par: un id sin su tenant leería el pedido de otro.</summary>
public sealed record TenantOrderKey(Guid TenantId, Guid OrderId);

/// <summary>Un cliente de un tenant, por la misma razón que <see cref="TenantOrderKey"/>.</summary>
public sealed record TenantCustomerKey(Guid TenantId, Guid CustomerId);

/// <summary>
/// Lo que el impacto de confirmar un sinónimo necesita de un pedido. <see cref="Status"/> es el nombre del enum de Quotations
/// (<c>Pending</c>, <c>Approved</c>, <c>Invoiced</c>, <c>Cancelled</c>), que el BFF manda tal cual.
/// </summary>
public sealed record OrderInsight(Guid TenantId, Guid OrderId, string OrderNumber, string Status, bool HasShippingParty, bool IsStorePickup);

/// <summary>Lo que el impacto necesita de un cliente para decidir si se completa (§4.4, desvío S7: versión 1).</summary>
public sealed record CustomerInsight(Guid TenantId, Guid CustomerId, bool IsComplete, bool IsMerged, bool IsActive, long Version);

/// <summary>
/// Una huella de lo que un sinónimo escribió, de los dos orígenes (O8: la corrección al confirmarlo y lo que casó al importarse).
/// <see cref="Target"/> es <c>Order</c> o <c>Customer</c>. <see cref="StillAsWritten"/>: el pedido conserva la parte de envío campo
/// por campo como la dejó la huella, o el cliente sigue en la versión de la huella. <see cref="OrderNumber"/> y
/// <see cref="OrderStatus"/> son nulos para un cliente, o si el pedido ya no existe.
/// </summary>
public sealed record CorrectionInsight(
    Guid TenantId, string Target, Guid TargetId, Guid CityId, string? OrderNumber, string? OrderStatus, bool StillAsWritten);

/// <summary>
/// <see cref="Orders"/> y <see cref="Customers"/>: huellas de los dos orígenes (O8). <see cref="PendingJobs"/> y
/// <see cref="FailedJobs"/>: trabajos de corrección en <c>Pending</c> y <c>Failed</c>; los <c>Applied</c> ya están en las huellas y
/// los <c>Skipped</c> no se hicieron.
/// </summary>
public sealed record SynonymCorrectionCounts(int Orders, int Customers, int PendingJobs, int FailedJobs);

/// <summary>
/// Spec 2026-10-26 D-C3b: lo que la consola de ciudades del operador lee de los tenants. Geography no ve Quotations, Customers,
/// Shopify ni Identity (<c>GeographyLayerTests</c>), así que el adaptador (<c>CityCorrectionInsights</c>, Bootstrapper) lee
/// <c>quotations</c>, <c>customers</c>, <c>shopify</c>, <c>tenancy</c> e <c>identity</c> POR NOMBRE, filtrando por <c>tenant_id</c>
/// donde la tabla lo tiene. Renombrar una columna de esas tablas compila y deja los endpoints de impacto en 500 (42703): lo amarra
/// <c>CityCorrectionInsightsTests</c> contra el esquema migrado.
/// </summary>
public interface ICityCorrectionInsights
{
    /// <summary>Sólo los que existen: una llave sin pedido (o de otro tenant) no vuelve.</summary>
    Task<IReadOnlyList<OrderInsight>> ReadOrdersAsync(IReadOnlyCollection<TenantOrderKey> keys, CancellationToken cancellationToken);

    /// <summary>Sólo los que existen, como <see cref="ReadOrdersAsync"/>.</summary>
    Task<IReadOnlyList<CustomerInsight>> ReadCustomersAsync(
        IReadOnlyCollection<TenantCustomerKey> keys, CancellationToken cancellationToken);

    /// <summary>Todas las huellas del sinónimo, de todos los tenants y de los dos orígenes.</summary>
    Task<IReadOnlyList<CorrectionInsight>> ReadCorrectionsAsync(CitySynonymId synonymId, CancellationToken cancellationToken);

    /// <summary>Una entrada por id pedido, también en cero: la lista de sinónimos dibuja los cuatro contadores siempre.</summary>
    Task<IReadOnlyDictionary<Guid, SynonymCorrectionCounts>> CountCorrectionsAsync(
        IReadOnlyCollection<CitySynonymId> synonymIds, CancellationToken cancellationToken);

    /// <summary>El nombre visible de cada tenant que existe.</summary>
    Task<IReadOnlyDictionary<Guid, string>> ReadTenantNamesAsync(IReadOnlyCollection<Guid> tenantIds, CancellationToken cancellationToken);

    /// <summary>El correo de cada usuario que existe (quién creó un sinónimo).</summary>
    Task<IReadOnlyDictionary<Guid, string>> ReadUserEmailsAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken);
}
