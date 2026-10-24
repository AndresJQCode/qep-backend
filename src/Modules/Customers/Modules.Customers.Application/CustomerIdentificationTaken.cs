using BuildingBlocks.Domain;
using Modules.Customers.Domain;

namespace Modules.Customers.Application;

/// <summary>
/// El documento ya es de otro cliente del tenant (activo o no, completo o no) y la respuesta dice de quién. Es un 422 de
/// dominio con el mismo código de siempre (<c>customers.customer.identification_taken</c>) más <c>existing</c>.
/// </summary>
/// <param name="existing">
/// Por qué viaja el dueño y no sólo el código: la pantalla que intenta guardar un documento repetido necesita ofrecer
/// «Ir al cliente» o explicar que está inactivo, y sin este dato tendría que adivinarlo con otra consulta. Tiene la forma de
/// <c>PosCustomerOptionResponse</c> más <c>Cuc</c>, que es <c>null</c> en un cliente incompleto.
/// </param>
public sealed class CustomerIdentificationTakenException(CustomerIdentificationOwnerDto existing)
    : DomainException(
        CustomerIdentificationTaken.Code,
        "Another customer in this tenant already uses that identification."),
      IHasProblemExtensions
{
    public IReadOnlyDictionary<string, object?> ProblemExtensions { get; } =
        new Dictionary<string, object?> { ["existing"] = existing };
}

internal static class CustomerIdentificationTaken
{
    public const string Code = "customers.customer.identification_taken";

    /// <summary>
    /// Relee al dueño del par (tipo, número) ya normalizado por el agregado, incluidos inactivos e incompletos. Va
    /// **después** de que el índice rechazó el guardado, nunca antes: el índice es el único árbitro de la unicidad y un
    /// pre-chequeo dejaría pasar la carrera. <c>CustomersUnitOfWork</c> ya limpió el tracker, así que la relectura corre en
    /// el mismo scope. Sin dueño (lo borraron en el medio) vuelve a lanzar la excepción original, sin <c>existing</c>.
    /// </summary>
    public static async Task<Exception> ResolveAsync(
        ICustomerRepository repository,
        Guid tenantId,
        Customer attempted,
        CustomersDomainException original,
        CancellationToken cancellationToken)
    {
        if (attempted.IdentificationType is not { } type || attempted.IdentificationNumber is not { } number)
        {
            return original;
        }

        var owners = await repository.FindExistingIdentificationsAsync(tenantId, [(type, number)], cancellationToken);
        if (!owners.TryGetValue((type, number), out var ownerId))
        {
            return original;
        }

        var owner = await repository.FindAsync(tenantId, ownerId, cancellationToken);
        if (owner is null)
        {
            return original;
        }

        return new CustomerIdentificationTakenException(new CustomerIdentificationOwnerDto(
            owner.Id.Value,
            owner.Name,
            type.ToWireValue(),
            number,
            owner.Cuc,
            owner.IsComplete,
            owner.IsActive));
    }
}
