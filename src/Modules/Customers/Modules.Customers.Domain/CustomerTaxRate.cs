namespace Modules.Customers.Domain;

/// <summary>
/// Una tasa de retención asignada a un cliente. Guarda sólo el id de la tasa: el catálogo vive en otro módulo y
/// Customers nunca lo referencia, así que no hay FK ni navegación. El tenant se repite para el índice «en uso».
/// </summary>
public sealed record CustomerTaxRate(Guid TenantId, Guid TaxRateId);
