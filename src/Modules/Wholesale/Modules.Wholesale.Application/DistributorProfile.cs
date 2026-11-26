using Modules.Wholesale.Domain;

namespace Modules.Wholesale.Application;

/// <summary>
/// Los datos que el mayorista llena del distribuidor, tal como se van a escribir: los de invitar, o los de editar ya mezclados
/// con lo guardado. Los dos casos pasan por las mismas reglas (<c>DistributorProfileValidator</c>).
/// </summary>
public interface IDistributorProfile
{
    string Email { get; }

    string BusinessName { get; }

    string Country { get; }

    string IdentificationType { get; }

    string IdentificationNumber { get; }

    string ContactName { get; }

    Guid? CityId { get; }

    string? CityName { get; }

    string? Address { get; }
}

/// <summary>Decisión del owner 2026-11-24: los datos de un distribuidor invitado después de aplicarle lo que trae el
/// <c>PUT</c>.</summary>
internal sealed record DistributorProfile(
    string Email,
    string BusinessName,
    string Country,
    string IdentificationType,
    string IdentificationNumber,
    string ContactName,
    Guid? CityId,
    string? CityName,
    string? Address)
    : IDistributorProfile
{
    /// <summary>
    /// Lo que el cuerpo no trae (<c>null</c>) conserva lo guardado, así que un formulario que no conoce estos campos sigue
    /// funcionando; la dirección se borra mandando <c>""</c>. Al cambiar de país no se arrastra la ciudad del anterior: hay que
    /// mandar la del nuevo.
    /// </summary>
    public static DistributorProfile Merge(Distributor distributor, UpdateDistributorCommand command)
    {
        var country = command.Country ?? distributor.Country;
        var sameCountry = Code(country) == distributor.Country;
        return new DistributorProfile(
            command.Email ?? distributor.Email,
            command.BusinessName ?? distributor.BusinessName,
            country,
            command.IdentificationType ?? distributor.IdentificationType,
            command.IdentificationNumber ?? distributor.IdentificationNumber,
            command.ContactName ?? distributor.ContactName,
            command.CityId ?? (sameCountry ? distributor.CityId : null),
            command.CityName ?? (sameCountry ? distributor.CityName : null),
            command.Address ?? distributor.Address);
    }

    /// <summary>
    /// Los campos que quedarían distintos de lo guardado, comparados como los guarda el dominio (recortados, el correo en
    /// minúsculas, vacío = <c>null</c>). Son los nombres que van a la auditoría: nunca los valores.
    /// </summary>
    public IReadOnlyList<string> ChangedFields(Distributor distributor)
    {
        (string Name, bool Changed)[] fields =
        [
            ("email", !string.Equals(Email.Trim(), distributor.Email, StringComparison.OrdinalIgnoreCase)),
            ("businessName", BusinessName.Trim() != distributor.BusinessName),
            ("country", Code(Country) != distributor.Country),
            ("identificationType", Code(IdentificationType) != distributor.IdentificationType),
            ("identificationNumber", IdentificationNumber.Trim() != distributor.IdentificationNumber),
            ("contactName", ContactName.Trim() != distributor.ContactName),
            ("cityId", (CityId == Guid.Empty ? null : CityId) != distributor.CityId),
            ("cityName", NullIfBlank(CityName) != distributor.CityName),
            ("address", NullIfBlank(Address) != distributor.Address),
        ];
        return [.. fields.Where(field => field.Changed).Select(field => field.Name)];
    }

    /// <summary>El país y el tipo de documento como los guarda el dominio.</summary>
    public static string Code(string value) => value.Trim().ToUpperInvariant();

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
