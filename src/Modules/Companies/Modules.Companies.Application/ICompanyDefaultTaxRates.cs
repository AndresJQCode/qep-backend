namespace Modules.Companies.Application;

/// <summary>
/// Las tasas de IVA por defecto de Colombia (spec 2026-11-17 §5.3, R6). Companies no ve Catalog ni Quotations: el adaptador
/// (<c>CompanyDefaultTaxRates</c>) vive en Bootstrapper, que crea las tasas en Catalog y fija la del envío en Quotations.
/// </summary>
public interface ICompanyDefaultTaxRates
{
    /// <summary>
    /// Idempotente. Crea «IVA 19 %», «IVA 5 %» y «Exento» si el tenant no tiene ninguna tasa de IVA activa de <c>CO</c>, y
    /// fija la del envío si está vacía. Se llama después de guardar la empresa: un fallo no la deshace.
    /// </summary>
    Task EnsureColombianVatAsync(Guid tenantId, Guid? actorId, CancellationToken cancellationToken);
}
