namespace Modules.Quotations.Application;

/// <summary>
/// Puerto hacia el módulo Customers (US-1/US-18: no se cotiza a un cliente sin CUC o inactivo).
///
/// Declarado acá y no como <c>ProjectReference</c> directo a
/// <c>Modules.Customers.Application</c>: ningún módulo de negocio referencia a otro directamente
/// (mismo criterio que <c>IProductImageLookup</c> en Catalog y <c>ICustomerGeographyLookup</c> en
/// Customers, CAT-05). El adaptador se implementa en <c>Bootstrapper</c>, que ya referencia a los
/// dos módulos y cuyo trabajo es exactamente cablearlos.
/// </summary>
public interface IQuotationCustomerLookup
{
    Task<QuotationCustomerRef?> FindAsync(
        Guid tenantId, Guid clientId, CancellationToken cancellationToken);

    /// <summary>
    /// Ids de cliente cuyo numero de identificacion (NIT/CC/...) contiene <paramref name="term"/>
    /// — lo usa el filtro de cotizaciones por NIT (ListQuotations.cs): resuelve el texto libre a
    /// ids antes de filtrar <c>Quotation.ClientId</c>, porque la cotizacion no guarda el NIT.
    /// Sin paginar, mismo criterio que <c>FindExistingIdentificationsAsync</c> en Customers.
    /// </summary>
    Task<IReadOnlySet<Guid>> SearchIdsByIdentificationAsync(
        Guid tenantId, string term, CancellationToken cancellationToken);

    /// <summary>
    /// El nombre de cada uno de estos clientes, en una sola consulta batch — lo usa el listado
    /// (ListQuotations.cs) para que cada fila viaje con el nombre del cliente ya resuelto. Sin
    /// esto, quien pinta la tabla tiene que pedir un cliente por fila: N+1 contra Customers por
    /// cada página del listado.
    ///
    /// Devuelve <c>QuotationCustomerRef</c> no, sólo el nombre: el listado no necesita CUC,
    /// teléfono ni el perfil de impuestos, y traerlos sería exponer datos del cliente que esa
    /// pantalla no muestra. Un id sin match simplemente no aparece en el resultado.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, string>> FindNamesAsync(
        Guid tenantId, IReadOnlyCollection<Guid> clientIds, CancellationToken cancellationToken);
}

// Name/Phone/Address se agregaron para el envío por WhatsApp (SendQuotation.cs): son los
// mismos tres campos que ya resuelve el frontend para armar el `wa.me` — se traen acá para
// que el backend pueda mandar la plantilla de Zenvia sin una segunda ida y vuelta al módulo
// Customers. Phone/Address quedan nullable porque lo son en el agregado `Customer` — el
// llamador decide qué hacer si faltan (SendQuotation.cs lo trata como "no se puede enviar").
// WithRetention/VatSurplus se agregan para el snapshot de totales al crear la cotizacion
// (Quotation.CustomerWithRetention/CustomerVatSurplus): igual criterio que Name/Phone/Address
// arriba, un pass-through desde Customer para no ir y volver al modulo por dos booleanos.
public sealed record QuotationCustomerRef(
    Guid Id,
    Guid TenantId,
    string Cuc,
    bool IsActive,
    string Name,
    string? Phone,
    string? Address,
    bool WithRetention,
    bool VatSurplus);
