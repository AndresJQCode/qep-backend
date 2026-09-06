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

    Guid CityId { get; }

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
        RuleFor(command => command.Phone)
            .MaximumLength(CustomerContactInfo.PhoneMaxLength);
        // Obligatoria desde la libreta de direcciones (028afe2): el alta y el PUT construyen con
        // ella la direccion principal del cliente, y una direccion sin calle no es una direccion.
        // La regla vive aca y no solo en el dominio para que el rechazo llegue como
        // validation.failed con el mapa errors -- el unico 422 que el formulario sabe leer para
        // marcar el input. Mide contra CustomerAddress y no contra CustomerContactInfo, que es
        // donde el campo vivia antes de la libreta.
        RuleFor(command => command.Address)
            .NotEmpty()
            .MaximumLength(CustomerAddress.AddressMaxLength);

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

        // La ciudad y la clasificacion son obligatorias: la Fase 3 las convirtio en FKs de primer
        // nivel, ya no texto libre opcional.
        RuleFor(command => command.CityId).NotEmpty();
        RuleFor(command => command.ClassificationId).NotEmpty();

        // Vacio es ausente para un campo opcional: el formulario manda "" cuando el usuario borra
        // el input, y sin el When() esa cadena vacia fallaria EmailAddress() y bloquearia el
        // guardado de un cliente que legitimamente no tiene correo.
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

    private static string Join(IReadOnlyCollection<string> values) =>
        string.Join(", ", values.Order(StringComparer.Ordinal));
}
