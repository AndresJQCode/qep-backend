using FluentValidation;
using Modules.Companies.Domain;

namespace Modules.Companies.Application;

/// <summary>
/// Lo que un POST y un PUT de empresa tienen en comun, para que las reglas de validacion puedan
/// escribirse **una sola vez**.
///
/// Existe por el hallazgo `D` de la revision de 4 lentes de CAT-04: los bloques de reglas estaban
/// duplicados textualmente entre el validador del POST y el del PUT, asi que corregir una sola
/// copia dejaba los dos verbos validando distinto — y ninguna prueba lo habria notado, porque
/// cada verbo tenia las suyas.
/// </summary>
public interface ICompanyWriteCommand
{
    string Name { get; }

    string AccountNumber { get; }

    string TaxId { get; }

    string? Phone { get; }

    string? Email { get; }

    string? Address { get; }
}

/// <summary>
/// Las reglas de escritura de empresa. El dominio hace cumplir las mismas y tiraria un 422 con un
/// solo codigo; el validador existe para que la respuesta lleve el mapa de errores **por campo**
/// que <c>ApiExceptionHandler</c> arma desde <c>ValidationException</c>.
///
/// Ese mapa no es un lujo: es lo unico que el formulario sabe leer.
/// <c>companyFieldErrors</c> (<c>features/companies/services/companies.api.ts</c>) descarta
/// cualquier 422 sin <c>errors</c>, asi que un codigo de dominio suelto deja el input sin marcar
/// y al usuario sin saber que corregir. Es la trampa que <c>register-tenant</c> ya documenta en
/// los dos repos.
///
/// Los nombres de propiedad viajan en PascalCase y asi los espera el consumidor
/// (<c>FIELD_BY_BACKEND_NAME</c>): Name, AccountNumber, TaxId, Phone, Email, Address.
/// </summary>
internal sealed class CompanyWriteRules : AbstractValidator<ICompanyWriteCommand>
{
    public CompanyWriteRules()
    {
        RuleFor(command => command.Name)
            .NotEmpty()
            .MaximumLength(Company.NameMaxLength);
        RuleFor(command => command.AccountNumber)
            .NotEmpty()
            .MaximumLength(Company.AccountNumberMaxLength);
        RuleFor(command => command.TaxId)
            .NotEmpty()
            .MaximumLength(Company.TaxIdMaxLength);
        RuleFor(command => command.Phone)
            .MaximumLength(CompanyContactInfo.PhoneMaxLength);
        RuleFor(command => command.Address)
            .MaximumLength(CompanyContactInfo.AddressMaxLength);

        // Vacio es ausente para un campo opcional: el formulario manda "" cuando el usuario borra
        // el input, y sin el When() esa cadena vacia fallaria EmailAddress() y bloquearia el
        // guardado de una empresa que legitimamente no tiene correo.
        RuleFor(command => command.Email)
            .MaximumLength(CompanyContactInfo.EmailMaxLength)
            .EmailAddress()
            .When(command => !string.IsNullOrWhiteSpace(command.Email));
    }
}
