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
}

// Name/Phone/Address se agregaron para el envío por WhatsApp (SendQuotation.cs): son los
// mismos tres campos que ya resuelve el frontend para armar el `wa.me` — se traen acá para
// que el backend pueda mandar la plantilla de Zenvia sin una segunda ida y vuelta al módulo
// Customers. Phone/Address quedan nullable porque lo son en el agregado `Customer` — el
// llamador decide qué hacer si faltan (SendQuotation.cs lo trata como "no se puede enviar").
public sealed record QuotationCustomerRef(
    Guid Id,
    Guid TenantId,
    string Cuc,
    bool IsActive,
    string Name,
    string? Phone,
    string? Address);
