using Microsoft.Extensions.DependencyInjection;
using Modules.Geography.Application;
using Modules.Geography.Domain;
using Modules.Shopify.Application;

namespace Bootstrapper;

/// <summary>
/// Spec 2026-10-26 D-C6: Geography → el trabajo de corrección de Shopify. Devuelve el sinónimo con su ciudad actual, borrados
/// incluidos: el trabajo de un sinónimo borrado termina <c>synonym_deleted</c>, y para eso tiene que verlo.
/// <para><b>Cada llamada lee en un scope propio</b>: <c>CitySynonymRepository</c> rastrea, y el procesador relee el sinónimo
/// después de guardar (por si el operador lo corrigió mientras corría). En el mismo scope esa segunda lectura devolvería la
/// instancia de la primera, con la ciudad vieja.</para>
/// </summary>
internal sealed class ShopifyCitySynonymLookup(IServiceScopeFactory scopes) : IShopifyCitySynonymLookup
{
    public async Task<ShopifyCitySynonymRef?> FindAsync(Guid synonymId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var synonym = await scope.ServiceProvider.GetRequiredService<ICitySynonymRepository>()
            .FindAsync(new CitySynonymId(synonymId), cancellationToken);
        return synonym is null
            ? null
            : new ShopifyCitySynonymRef(synonym.Id.Value, synonym.DepartmentId.Value, synonym.CityId.Value, synonym.IsDeleted);
    }
}
