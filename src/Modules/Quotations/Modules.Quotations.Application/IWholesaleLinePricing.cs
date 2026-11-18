namespace Modules.Quotations.Application;

/// <summary>
/// D-5: el precio de lista que tiene cada variante para el distribuidor de un pedido mayorista (IVA incluido, con el redondeo
/// del mayorista), para la línea que el mayorista agrega al editar. Sin mirar el estado de la relación ni la visibilidad: es el
/// pedido del propio mayorista. Sin relación, o una variante sin precio en <paramref name="currency"/>, no viene y la línea
/// toma el precio base del catálogo. Adaptador en Bootstrapper sobre Wholesale.
/// </summary>
public interface IWholesaleLinePricing
{
    Task<IReadOnlyDictionary<Guid, decimal>> ListPricesAsync(
        Guid tenantId,
        Guid buyerTenantId,
        string currency,
        IReadOnlyCollection<Guid> variantIds,
        CancellationToken cancellationToken);
}
