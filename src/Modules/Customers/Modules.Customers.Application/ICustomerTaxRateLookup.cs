namespace Modules.Customers.Application;

/// <summary>
/// Una tasa del catálogo de impuestos tal como Customers la necesita: para validar que se puede asignar a un cliente y para
/// pintarla en su ficha. <c>Withholds</c> es «esta tasa resta del total»; <c>Type</c> viaja como texto (<c>"Retefuente"</c>,
/// <c>"ReteICA"</c>, <c>"IVA"</c>).
/// </summary>
public sealed record CustomerTaxRateRef(
    Guid Id,
    string Name,
    string Type,
    string Country,
    decimal Percentage,
    bool IsActive,
    bool Withholds);

/// <summary>
/// Puerto de Customers hacia el catálogo de impuestos (spec impuestos por tipo §7.4). Customers nunca referencia Catalog: el
/// composition root cablea el adaptador. Todo método recibe <c>tenantId</c> y sólo ve las tasas de ese tenant, así que una
/// tasa ajena se comporta como una que no existe.
/// </summary>
public interface ICustomerTaxRateLookup
{
    /// <summary>Las tasas pedidas que existen en el tenant, por id. Las que faltan simplemente no están en el resultado.</summary>
    Task<IReadOnlyDictionary<Guid, CustomerTaxRateRef>> FindManyAsync(
        Guid tenantId, IReadOnlyCollection<Guid> taxRateIds, CancellationToken cancellationToken);

    /// <summary>Las tasas activas del tenant que retienen. La usa el Excel para resolver una columna por nombre.</summary>
    Task<IReadOnlyList<CustomerTaxRateRef>> ListWithholdingAsync(Guid tenantId, CancellationToken cancellationToken);
}
