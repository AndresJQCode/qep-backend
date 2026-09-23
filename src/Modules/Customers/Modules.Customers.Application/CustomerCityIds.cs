using Modules.Customers.Domain;

namespace Modules.Customers.Application;

/// <summary>
/// El domicilio de un cliente tiene dos formas desde que existe el pais: una ciudad DIVIPOLA
/// (Colombia, <see cref="Customer.CityId"/>) o un nombre escrito a mano (el resto del mundo,
/// <see cref="Customer.CityName"/>).
///
/// Estos dos helpers son el unico lugar donde esa bifurcacion se resuelve al proyectar.
/// <c>CustomerMapping</c>, <c>ListCustomers</c> y <c>ExportCustomers</c> repetian la misma linea
/// cuando la ciudad era obligatoria; ahora que puede faltar, repetirla tres veces seria repetir
/// tres veces la chance de tirar abajo la ficha de un cliente de afuera.
/// </summary>
internal static class CustomerCityIds
{
    /// <summary>
    /// La ciudad de domicilio que hay que resolver contra Geography, o nada si el cliente no es
    /// de Colombia. Se devuelve una secuencia y no un <c>Guid?</c> para que los llamadores la
    /// concatenen con las ciudades de la libreta sin desarmar el nulo cada vez.
    /// </summary>
    public static IEnumerable<Guid> OfDomicile(Customer customer) =>
        customer.CityId is { } cityId ? [cityId] : [];

    /// <summary>
    /// La ciudad de domicilio ya resuelta, o <c>null</c> para un cliente de afuera — que no tiene
    /// uno y cuya ciudad viaja en <see cref="Customer.CityName"/>.
    ///
    /// Un cliente colombiano cuya ciudad **no** aparece es corrupcion de datos y no entrada de
    /// usuario (la FK de base garantiza que exista), asi que sigue siendo un
    /// <see cref="InvalidOperationException"/> (500) y no un <c>CustomersDomainException</c>
    /// (422): no hay ningun campo del request que el llamador pueda corregir.
    /// </summary>
    public static CustomerCityRef? ResolveDomicile(
        Customer customer,
        IReadOnlyDictionary<Guid, CustomerCityRef> citiesById)
    {
        if (customer.CityId is not { } cityId)
        {
            return null;
        }

        return citiesById.TryGetValue(cityId, out var city)
            ? city
            : throw new InvalidOperationException(
                $"City '{cityId}' referenced by customer '{customer.Id}' was not found.");
    }
}
