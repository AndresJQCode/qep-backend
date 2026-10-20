using Modules.Catalog.Domain;

namespace Modules.Catalog.Application;

/// <summary>
/// Las opciones de la biblioteca que un guardado de producto necesita, por id. Sólo del tenant:
/// una opción ajena no aparece, y <see cref="ProductVariantReferences"/> la reporta como
/// inexistente.
/// </summary>
internal static class ProductOptionLibrary
{
    public static async Task<IReadOnlyDictionary<Guid, OptionDefinition>> LoadAsync(
        IOptionDefinitionRepository repository,
        Guid tenantId,
        IEnumerable<Guid> optionIds,
        CancellationToken cancellationToken)
    {
        var ids = optionIds.Distinct().Select(id => new OptionDefinitionId(id)).ToArray();
        var options = await repository.ListByIdsAsync(tenantId, ids, cancellationToken);
        return options.ToDictionary(option => option.Id.Value);
    }
}
