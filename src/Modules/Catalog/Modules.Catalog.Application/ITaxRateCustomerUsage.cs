namespace Modules.Catalog.Application;

/// <summary>
/// Puerto de Catalog hacia Customers: una tasa asignada a un cliente está «en uso» y no se desactiva ni se borra. No hay FK
/// entre los dos esquemas (Catalog nunca referencia otro módulo de negocio), así que esta consulta es la única barrera.
/// </summary>
public interface ITaxRateCustomerUsage
{
    Task<bool> AnyCustomerWithTaxRateAsync(Guid tenantId, Guid taxRateId, CancellationToken cancellationToken);
}
