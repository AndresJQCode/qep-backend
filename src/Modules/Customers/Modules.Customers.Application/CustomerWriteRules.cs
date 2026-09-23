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

    string IdentificationType { get; }

    string IdentificationNumber { get; }

    string? Phone { get; }

    string? Email { get; }

    string? Address { get; }

    string Country { get; }

    Guid? CityId { get; }

    string? CityName { get; }

    Guid ClassificationId { get; }
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
/// <c>CityId</c> y <c>ClassificationId</c> solo se comprueban **no vacios** aca: que la fila
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
        RuleFor(command => command.IdentificationNumber)
            .NotEmpty()
            .MaximumLength(CustomerIdentification.NumberMaxLength);
        // Telefono y correo son obligatorios (CustomerContactInfo): se rechazan aca tambien para
        // que el 422 lleve el mapa errors por campo y no solo el codigo de dominio.
        RuleFor(command => command.Phone)
            .NotEmpty()
            .MaximumLength(CustomerContactInfo.PhoneMaxLength);
        // Obligatoria: es el domicilio del cliente (CustomerContactInfo.Address) y en el alta
        // ademas siembra la primera fila de la libreta. La regla vive aca y no solo en el dominio
        // para que el rechazo llegue como validation.failed con el mapa errors -- el unico 422 que
        // el formulario sabe leer para marcar el input.
        RuleFor(command => command.Address)
            .NotEmpty()
            .MaximumLength(CustomerContactInfo.AddressMaxLength);

        // El tipo de documento es obligatorio y cerrado. Se comprueba contra la misma tabla que el
        // dominio (IdentificationTypeParser) y no contra una lista repetida aca: dos listas de
        // valores validos que alguien puede ampliar por separado terminan discrepando, y la que
        // gana es la del dominio — con un 422 sin mapa de errores.
        RuleFor(command => command.IdentificationType)
            .NotEmpty()
            .Must(IsSupportedIdentificationType)
            .WithMessage(command =>
                $"The identification type must be one of {Join(IdentificationTypeParser.SupportedWireValues)}.")
            .When(command => !string.IsNullOrWhiteSpace(command.IdentificationType));

        // La clasificacion es obligatoria: la Fase 3 la convirtio en una FK de primer nivel, ya no
        // texto libre opcional.
        RuleFor(command => command.ClassificationId).NotEmpty();

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

        // Cual de los dos carriles de ciudad hace falta lo decide el pais. DIVIPOLA solo describe
        // Colombia, asi que un cliente de afuera no tiene CityId que mandar y escribe su ciudad.
        // Marcar el campo correcto importa: el formulario pinta un combobox o un input de texto
        // segun el pais, y un error apuntando al campo que no esta en pantalla no se ve.
        RuleFor(command => command.CityId)
            .NotEmpty()
            .WithMessage("The city is required.")
            .When(IsColombian);
        RuleFor(command => command.CityName)
            .NotEmpty()
            .WithMessage("The city is required.")
            .When(command => !IsColombian(command));
        RuleFor(command => command.CityName)
            .MaximumLength(CustomerContactInfo.CityNameMaxLength)
            .When(command => !string.IsNullOrWhiteSpace(command.CityName));

        // Dos RuleFor y no uno encadenado: el When() gobierna toda la cadena que lo precede
        // (ApplyConditionTo.AllValidators), asi que un NotEmpty() delante quedaria apagado
        // justamente cuando el correo viene vacio. El When() del formato evita que un correo vacio
        // reporte ademas "no es una direccion valida" encima de "es obligatorio".
        RuleFor(command => command.Email)
            .NotEmpty();
        RuleFor(command => command.Email)
            .MaximumLength(CustomerContactInfo.EmailMaxLength)
            .EmailAddress()
            .When(command => !string.IsNullOrWhiteSpace(command.Email));
    }

    // Un pais ausente cuenta como Colombia a los efectos de **cual** ciudad exigir. No es
    // adivinar: el pais tiene su propia regla NotEmpty que ya marca el campo, y dejar los dos
    // carriles apagados haria que un cuerpo sin pais tampoco reporte la ciudad — el formulario
    // marcaria un solo campo por vez y el usuario corregiria de a uno. Ademas conserva el
    // comportamiento anterior al pais para cualquier llamador que todavia no lo mande.
    private static bool IsColombian(ICustomerWriteCommand command) =>
        string.IsNullOrWhiteSpace(command.Country)
        || string.Equals(
            command.Country.Trim(),
            Customer.ColombiaCountryCode,
            StringComparison.OrdinalIgnoreCase);

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

    private static string Join(IReadOnlyCollection<string> values) =>
        string.Join(", ", values.Order(StringComparer.Ordinal));
}
