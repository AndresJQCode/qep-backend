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

    string? Department { get; }

    string? City { get; }

    string? Classification { get; }
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
/// Address, Department, City, Classification, PriceListId, WithRetention.
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
        RuleFor(command => command.Address)
            .MaximumLength(CustomerContactInfo.AddressMaxLength);
        RuleFor(command => command.Department)
            .MaximumLength(CustomerContactInfo.DepartmentMaxLength);
        RuleFor(command => command.City)
            .MaximumLength(CustomerContactInfo.CityMaxLength);

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

        // La clasificacion es opcional: vacio es ausente, igual que en los demas campos opcionales.
        // Un valor presente pero desconocido si falla — es un dato mal escrito, no uno ausente.
        RuleFor(command => command.Classification)
            .Must(IsSupportedClassification)
            .WithMessage(command =>
                $"The classification must be one of {Join(CustomerClassificationParser.SupportedWireValues)}.")
            .When(command => !string.IsNullOrWhiteSpace(command.Classification));

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

    private static bool IsSupportedClassification(string? value)
    {
        try
        {
            CustomerClassificationParser.Parse(value);
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
