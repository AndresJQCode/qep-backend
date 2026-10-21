using BuildingBlocks.Application;
using Modules.Catalog.Domain;

namespace Modules.Catalog.Application;

// La biblioteca de opciones del tenant (spec 2026-10-20 §3.1).
//
// BFF: cada opción y cada valor llevan CanDelete. La pantalla ofrece «Eliminar» o «Desactivar» sin
// adivinar —una opción o un valor que alguna variante usa no se borra— y sin pedir el borrado para
// enterarse por el 409. Un valor viaja como { id, name } y no { id, value } para que se lea igual
// que las opciones del producto (ProductResponse.options[].values).
public sealed record OptionsResponse(IReadOnlyList<OptionResponse> Items);

public sealed record OptionResponse(
    Guid Id,
    string Name,
    bool IsActive,
    bool CanDelete,
    IReadOnlyList<OptionValueResponse> Values,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record OptionValueResponse(Guid Id, string Name, bool IsActive, bool CanDelete);

// Una opción creada por el tenant nace activa: la crea porque la va a usar. Las inactivas son sólo
// las predefinidas, para que no le aparezcan a quien no usa variantes.
public sealed record CreateOptionRequest(string Name, IReadOnlyList<string>? Values);

// El PUT reemplaza la opción entera: nombre, estado y la lista de valores en orden. Un valor con id
// es uno guardado; sin id, nuevo; uno guardado que no viene se borra si nadie lo usa (si alguna
// variante lo usa, 409 catalog.option.in_use: se desactiva con IsActive = false). Desactivar la
// opción también va por acá, con IsActive, y no por un endpoint aparte: el formulario ya tiene el
// cuerpo entero y repinta lo que vuelve.
//
// IsActive es bool? y el validador lo exige (NotNull), en la opción y en cada valor: con bool, un
// cuerpo que lo omite llegaba como false y desactivaba en silencio la opción o el valor. Así sale
// 422 validation.failed con errors["IsActive"] o errors["Values[i].IsActive"] y no cambia nada.
public sealed record UpdateOptionRequest(
    string Name,
    bool? IsActive,
    IReadOnlyList<OptionValueRequest>? Values);

public sealed record OptionValueRequest(Guid? Id, string Name, bool? IsActive);

/// <summary>
/// Qué opciones y valores usa alguna variante del tenant. «En uso» es una consulta dentro de
/// Catalog (las variantes viven acá), sin puertos (spec §3.1).
/// </summary>
public sealed record OptionUsage(IReadOnlySet<Guid> OptionIds, IReadOnlySet<Guid> ValueIds)
{
    public bool IsInUse(OptionDefinition option) =>
        OptionIds.Contains(option.Id.Value) || option.Values.Any(value => ValueIds.Contains(value.Id));
}

// Todo método recibe tenantId primero, como el resto de los repositorios del módulo.
public interface IOptionDefinitionRepository
{
    Task<IReadOnlyList<OptionDefinition>> ListAsync(Guid tenantId, CancellationToken cancellationToken);

    Task<OptionDefinition?> FindAsync(
        Guid tenantId,
        OptionDefinitionId optionId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Las opciones de esos ids, con sus valores, sin rastreo y sólo del tenant: un id que no existe
    /// o es de otro tenant no aparece. El producto valida sus opciones y arma los nombres visibles
    /// con esto.
    /// </summary>
    Task<IReadOnlyList<OptionDefinition>> ListByIdsAsync(
        Guid tenantId,
        IReadOnlyCollection<OptionDefinitionId> optionIds,
        CancellationToken cancellationToken);

    Task<OptionUsage> GetUsageAsync(Guid tenantId, CancellationToken cancellationToken);

    void Add(OptionDefinition definition);

    void Remove(OptionDefinition definition);
}

/// <summary>
/// Siembra «Talla» y «Color» inactivas la primera vez que el tenant abre la biblioteca, como
/// <c>PipelineProvisioner</c> crea el embudo: Tenancy no publica nada al crear un tenant que
/// Catalog escuche. Una sola vez por tenant, aunque después borre una predefinida.
/// </summary>
public interface IOptionLibraryProvisioner
{
    Task EnsureAsync(Guid tenantId, CancellationToken cancellationToken);
}

internal static class OptionNotFound
{
    public static ResourceNotFoundException For(Guid optionId) =>
        new("catalog.option.not_found", $"Option '{optionId}' was not found.");
}

internal static class OptionInUse
{
    public static ResourceConflictException Create() =>
        new(
            "catalog.option.in_use",
            "The option or value is used by at least one variant: deactivate it instead.");
}

internal static class OptionMapping
{
    public static OptionResponse ToResponse(this OptionDefinition option, OptionUsage usage) => new(
        option.Id.Value,
        option.Name,
        option.IsActive,
        !usage.IsInUse(option),
        option.Values
            .OrderBy(value => value.Position)
            .Select(value => new OptionValueResponse(
                value.Id, value.Value, value.IsActive, !usage.ValueIds.Contains(value.Id)))
            .ToArray(),
        option.CreatedAt,
        option.UpdatedAt);
}
