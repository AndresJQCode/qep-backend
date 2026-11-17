namespace Modules.Companies.Domain;

/// <summary>
/// Una tasa de retención municipal (ReteICA) asignada a la empresa facturadora (spec ReteICA por empresa §7.3). Guarda sólo
/// el id de la tasa: el catálogo vive en otro módulo y Companies nunca lo referencia, así que no hay FK ni navegación. El
/// tenant se repite para el índice «en uso». Gemelo de <c>CustomerTaxRate</c>.
/// </summary>
public sealed record CompanyTaxRate(Guid TenantId, Guid TaxRateId);
