using BuildingBlocks.Domain.Identification;
using FluentValidation;
using Modules.Customers.Domain;

namespace Modules.Customers.Application;

/// <summary>
/// Lo que un POST y un PUT de cliente tienen en comun, para que las reglas de validacion puedan
/// escribirse **una sola vez**.
///
/// Existe por el hallazgo `D` de la revision de 4 lentes de CAT-04: los bloques de reglas estaban
/// duplicados textualmente entre el validador del POST y el del PUT, asi que corregir una sola
/// copia dejaba los dos verbos validando distinto — y ninguna prueba lo habria notado, porque cada
/// verbo tenia las suyas.
/// </summary>
public interface ICustomerWriteCommand
{
    string Name { get; }

    string? IdentificationType { get; }

    string? IdentificationNumber { get; }

    string? Phone { get; }

    string? Email { get; }

    string? Address { get; }

    string Country { get; }

    Guid? CityId { get; }

    string? CityName { get; }

    Guid? ClassificationId { get; }

    /// <summary>Spec 2026-10-26: <c>"Natural"</c> o <c>"Juridica"</c>; en blanco es «sin tipo».</summary>
    string? PersonType { get; }
}

/// <summary>
/// Las reglas de escritura de cliente. El dominio hace cumplir las mismas y tiraria un 422 con un
/// solo codigo; el validador existe para que la respuesta lleve el mapa de errores **por campo**
/// que <c>ApiExceptionHandler</c> arma desde <c>ValidationException</c>.
///
/// Ese mapa no es un lujo: es lo unico que el formulario sabe leer. <c>customerFieldErrors</c>
/// (<c>features/customers/services/customers.api.ts</c>) descarta cualquier 422 sin <c>errors</c>,
/// asi que un codigo de dominio suelto deja el input sin marcar y al usuario sin saber que
/// corregir. Es la trampa que <c>register-tenant</c> ya documenta en los dos repos.
///
/// Los nombres de propiedad viajan en PascalCase y asi los espera el consumidor
/// (<c>FIELD_BY_BACKEND_NAME</c>): Name, IdentificationType, IdentificationNumber, Phone, Email,
/// Address, CityId, ClassificationId, WithRetention, VatSurplus.
///
/// <c>CityId</c> y <c>ClassificationId</c> solo se comprueban **no vacios** aca (la clasificacion,
/// solo si viene: es opcional): que la fila
/// exista y sea del tenant lo resuelve el handler (para armar el CUC) y, en la carrera, la FK de
/// base — mismo criterio que el resto de las FKs de este repo, sin pre-chequeo de existencia en
/// el validador.
/// </summary>
internal sealed class CustomerWriteRules : AbstractValidator<ICustomerWriteCommand>
{
    public CustomerWriteRules()
    {
        RuleFor(command => command.Name)
            .NotEmpty()
            .MaximumLength(Customer.NameMaxLength);
        // El documento ya no es obligatorio aca: si hace falta lo decide la politica del tenant
        // (CustomerFieldPolicyRequirement), que tambien exige que tipo y numero vengan juntos.
        RuleFor(command => command.IdentificationNumber)
            .MaximumLength(CustomerIdentification.NumberMaxLength);
        // El telefono es obligatorio (CustomerContactInfo): se rechaza aca tambien para que el 422
        // lleve el mapa errors por campo y no solo el codigo de dominio.
        RuleFor(command => command.Phone)
            .NotEmpty()
            .MaximumLength(CustomerContactInfo.PhoneMaxLength);
        // Opcional: es el domicilio del cliente (CustomerContactInfo.Address), y sin calle el alta
        // simplemente no siembra la libreta (CustomerMapping.ToFirstAddress). Solo se limita el
        // largo, y aca y no solo en el dominio para que el rechazo llegue como validation.failed
        // con el mapa errors -- el unico 422 que el formulario sabe leer para marcar el input.
        RuleFor(command => command.Address)
            .MaximumLength(CustomerContactInfo.AddressMaxLength);

        // El tipo de documento es obligatorio y cerrado. Se comprueba contra la misma tabla que el
        // dominio (IdentificationTypeParser) y no contra una lista repetida aca: dos listas de
        // valores validos que alguien puede ampliar por separado terminan discrepando, y la que
        // gana es la del dominio — con un 422 sin mapa de errores.
        RuleFor(command => command.IdentificationType)
            .Must(IsSupportedIdentificationType)
            .WithMessage(command =>
                $"The identification type must be one of {Join(IdentificationTypeParser.SupportedWireValues)}.")
            .When(command => !string.IsNullOrWhiteSpace(command.IdentificationType));

        // Regla aparte de la anterior, y no un Must más en la misma cadena: así un código desconocido
        // sólo reporta la lista y un país mal formado sólo reporta el país. Corre cuando el código es
        // conocido y el país tiene forma de ISO-3166-1 alpha-2.
        RuleFor(command => command.IdentificationType)
            .Must((command, type) => DocumentTypesByCountry.IsAllowed(type, command.Country))
            .WithMessage("The identification type is not allowed for the country.")
            .When(command => DocumentTypesByCountry.IsKnown(command.IdentificationType)
                && command.Country is { Length: CustomerContactInfo.CountryLength } country
                && country.All(char.IsAsciiLetter));

        // Spec 2026-10-26: el tipo de persona es opcional, pero uno que no se reconoce se rechaza aca para que el 422
        // lleve el mapa errors con la clave PersonType. Se comprueba contra la misma tabla que el dominio
        // (PersonTypeParser), por lo mismo que el tipo de documento.
        RuleFor(command => command.PersonType)
            .Must(IsSupportedPersonType)
            .WithMessage(
                $"The person type must be one of {Join(PersonTypeParser.SupportedWireValues)}.")
            .When(command => !string.IsNullOrWhiteSpace(command.PersonType));

        // La clasificacion es opcional (decision del owner, 2026-10-19): sin ella el CUC no lleva
        // prefijo. Lo que se rechaza es Guid.Empty, que es un id roto y no una forma de decir
        // "ninguna" — la ausencia se dice con null.
        RuleFor(command => command.ClassificationId).NotEqual(Guid.Empty);

        // El pais es obligatorio y son dos letras (ISO-3166-1 alpha-2). Dos RuleFor por la misma
        // razon que el correo: el When() gobierna toda la cadena que lo precede, asi que un
        // NotEmpty() delante del Length quedaria apagado justo cuando el pais viene vacio.
        RuleFor(command => command.Country).NotEmpty();
        RuleFor(command => command.Country)
            .Length(CustomerContactInfo.CountryLength)
            .Must(country => country.All(char.IsAsciiLetter))
            .WithMessage(
                $"The country must be an ISO-3166-1 alpha-2 code ({CustomerContactInfo.CountryLength} letters).")
            .When(command => !string.IsNullOrWhiteSpace(command.Country));

        // La ciudad ya no es obligatoria aca: si hace falta (y cual de los dos carriles, CityId o CityName, segun el
        // pais) lo decide la politica del tenant en CustomerFieldPolicyRequirement. Lo que se sigue rechazando aca es
        // Guid.Empty, un id roto que no es una forma de decir "ninguna" (la ausencia se dice con null).
        RuleFor(command => command.CityId).NotEqual(Guid.Empty);
        RuleFor(command => command.CityName)
            .MaximumLength(CustomerContactInfo.CityNameMaxLength)
            .When(command => !string.IsNullOrWhiteSpace(command.CityName));

        // El correo es opcional (CustomerContactInfo): vacio no es error, y el When() evita que un
        // correo vacio se reporte como "no es una direccion valida".
        RuleFor(command => command.Email)
            .MaximumLength(CustomerContactInfo.EmailMaxLength)
            .EmailAddress()
            .When(command => !string.IsNullOrWhiteSpace(command.Email));
    }

    private static bool IsSupportedIdentificationType(string? value)
    {
        try
        {
            IdentificationTypeParser.Parse(value);
            return true;
        }
        catch (CustomersDomainException)
        {
            return false;
        }
    }

    private static bool IsSupportedPersonType(string? value)
    {
        try
        {
            PersonTypeParser.ParseOptional(value);
            return true;
        }
        catch (CustomersDomainException)
        {
            return false;
        }
    }

    private static string Join(IReadOnlyCollection<string> values) =>
        string.Join(", ", values.Order(StringComparer.Ordinal));
}
