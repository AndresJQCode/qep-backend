namespace Modules.Catalog.Application;

/// <summary>
/// Puerto de Catalog hacia Companies: una tasa municipal asignada a una empresa facturadora está «en uso» y no se desactiva
/// ni se borra (spec ReteICA por empresa §7.2). Un puerto por módulo consultado, como <see cref="ITaxRateCustomerUsage"/>.
/// No hay FK entre los esquemas, así que esta consulta es la única barrera.
/// </summary>
public interface ITaxRateCompanyUsage
{
    Task<bool> AnyCompanyWithTaxRateAsync(Guid tenantId, Guid taxRateId, CancellationToken cancellationToken);
}
