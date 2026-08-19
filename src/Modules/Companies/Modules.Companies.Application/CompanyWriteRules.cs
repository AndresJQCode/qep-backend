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

    IReadOnlyList<CompanyBankAccountPayload> BankAccounts { get; }

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
/// (<c>FIELD_BY_BACKEND_NAME</c>): Name, TaxId, Phone, Email, Address. Los de la coleccion llegan
/// indexados —<c>BankAccounts[0].AccountNumber</c>— porque marcar la fila equivocada de un campo
/// repetido es lo mismo que no marcar ninguna.
/// </summary>
internal sealed class CompanyWriteRules : AbstractValidator<ICompanyWriteCommand>
{
    public CompanyWriteRules()
    {
        RuleFor(command => command.Name)
            .NotEmpty()
            .MaximumLength(Company.NameMaxLength);
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

        // NotEmpty cubre los dos casos que el formulario puede producir: la lista ausente del JSON
        // —que System.Text.Json deja en null— y la lista vacia de quien quito la ultima fila. Sin
        // esta regla, la primera llega al dominio como null y sale como 500.
        RuleFor(command => command.BankAccounts)
            .NotEmpty()
            .WithMessage("The company needs at least one bank account.");
        RuleFor(command => command.BankAccounts)
            .Must(accounts => accounts!.Count <= CompanyBankAccount.MaxPerCompany)
            .WithMessage(
                $"A company cannot have more than {CompanyBankAccount.MaxPerCompany} bank accounts.")
            .When(command => command.BankAccounts is not null);

        // Cada fila con sus propias reglas, para que el error salga como BankAccounts[2].Currency
        // y el formulario pueda marcar exactamente esa fila.
        RuleForEach(command => command.BankAccounts).ChildRules(account =>
        {
            account.RuleFor(value => value.BankName)
                .NotEmpty()
                .MaximumLength(CompanyBankAccount.BankNameMaxLength);
            account.RuleFor(value => value.AccountNumber)
                .NotEmpty()
                .MaximumLength(CompanyBankAccount.AccountNumberMaxLength);

            // Tres letras, ISO 4217. Se comprueba con Length + All(IsLetter) y no con una
            // expresion regular por el mismo criterio con el que CompanyContactInfo evita una para
            // el correo: no hace falta un patron para decidir si algo tiene tres letras.
            account.RuleFor(value => value.Currency)
                .Must(currency =>
                    currency is not null &&
                    currency.Trim().Length == CompanyBankAccount.CurrencyLength &&
                    currency.Trim().All(char.IsLetter))
                .WithMessage("The currency must be a three-letter ISO 4217 code.");
        });

        // El duplicado se comprueba aca **ademas** de en el dominio, y no solo alla, porque el
        // dominio tira un codigo suelto sin mapa de errores: el formulario mostraria "revisa los
        // datos marcados" sin marcar ninguno. La clave tiene que ser la misma que usa
        // CompanyBankAccount.DeduplicationKey — banco sin distinguir caja, numero distinguiendola,
        // moneda en mayusculas— o el validador dejaria pasar lo que el dominio rechaza y el 422
        // saldria sin mapa igual.
        RuleFor(command => command.BankAccounts)
            .Must(HasNoDuplicates)
            .WithMessage("The same bank account is listed more than once.")
            .When(command => command.BankAccounts is not null);
    }

    private static bool HasNoDuplicates(IReadOnlyList<CompanyBankAccountPayload> accounts)
    {
        var keys = accounts.Select(account => (
            Bank: account.BankName?.Trim().ToLowerInvariant() ?? string.Empty,
            Number: account.AccountNumber?.Trim() ?? string.Empty,
            Currency: account.Currency?.Trim().ToUpperInvariant() ?? string.Empty));

        return keys.Distinct().Count() == accounts.Count;
    }
}
