using Modules.Catalog.Domain;

namespace Modules.Catalog.Application;

/// <summary>
/// Resuelve el <c>taxRateId</c> que llega en un comando contra el catálogo de tasas **del tenant
/// del producto**.
///
/// Existe porque la foreign key no alcanza. `catalog.products.tax_rate_id` referencia a
/// `catalog.tax_rates(id)`, y esa constraint sólo garantiza que la fila exista: **no sabe nada de
/// tenants**. Sin esta comprobación, un producto del tenant A puede apuntar a una tasa del tenant
/// B y la base lo acepta sin una queja, porque la fila está.
///
/// Es una fuga entre tenants que ninguna prueba de status HTTP encuentra: la respuesta sería un
/// 201 perfectamente normal. La cubre CA-CAT-04-07.
/// </summary>
internal static class ProductTaxRateResolver
{
    public static async Task<TaxRateId?> ResolveAsync(
        ITaxRateRepository repository,
        Guid tenantId,
        Guid? taxRateId,
        CancellationToken cancellationToken)
    {
        if (taxRateId is null)
        {
            return null;
        }

        var candidate = new TaxRateId(taxRateId.Value);

        // FindAsync recibe tenantId como primer parámetro justamente para esto: el filtro de
        // tenant es parte de la consulta, no un chequeo que el llamador se pueda olvidar.
        var taxRate = await repository.FindAsync(tenantId, candidate, cancellationToken);

        // Una tasa inactiva SÍ se acepta: inactivarla no debe romper los productos que ya la
        // usaban, ni impedir corregir uno mientras se decide su reemplazo. Lo que se rechaza es
        // que no exista o que sea de otro tenant — y las dos dan el mismo código a propósito,
        // porque distinguirlas le confirmaría al llamador que el id existe en otro tenant.
        return taxRate is null
            ? throw new CatalogDomainException(
                "catalog.product.tax_rate_not_found",
                "The tax rate was not found in this tenant.")
            : candidate;
    }
}
