using FluentValidation;
using Modules.Companies.Domain;
using Modules.Tenancy.Application;

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

    string? Country { get; }

    string? DocumentType { get; }

    Guid? CityId { get; }

    string? CityName { get; }

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
/// (<c>FIELD_BY_BACKEND_NAME</c>): Name, TaxId, Country, DocumentType, CityId, CityName, Phone, Email, Address. Los de la coleccion llegan
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

        // Dos RuleFor por campo: el When() gobierna toda la cadena que lo precede, y un NotEmpty()
        // delante quedaría apagado justo cuando el valor viene vacío.
        // País y tipo de documento son obligatorios en POST y PUT: la ventana que resolvía un
        // cuerpo sin país (Colombia/NIT o el país guardado) se cerró el 2026-11-08, por decisión
        // del owner, al desplegar backend y frontend juntos.
        RuleFor(command => command.Country)
            .NotEmpty();
        RuleFor(command => command.Country)
            .Must(country => IsValidCountry(country!))
            .WithMessage(
                $"The country must be an ISO-3166-1 alpha-2 code ({Company.CountryLength} letters).")
            .When(command => !string.IsNullOrWhiteSpace(command.Country));

        // El tipo de documento tiene que estar permitido para el país (misma tabla que el
        // dominio, no una lista repetida aquí).
        RuleFor(command => command.DocumentType)
            .NotEmpty();
        RuleFor(command => command.DocumentType)
            .Must(type => CompanyDocumentTypes.TryParse(type, out _))
            .WithMessage("The document type is not recognized.")
            .When(command => !string.IsNullOrWhiteSpace(command.DocumentType));
        RuleFor(command => command.DocumentType)
            .Must((command, type) =>
                CompanyDocumentTypes.TryParse(type, out var parsed)
                && CompanyDocumentTypes.AllowedFor(EffectiveCountry(command)).Contains(parsed))
            .WithMessage("The document type is not allowed for the country.")
            .When(command =>
                CompanyDocumentTypes.TryParse(command.DocumentType, out _) && HasValidCountry(command));

        // El país decide cuál de los dos carriles de ciudad se exige, y el error tiene que caer en
        // el campo que el formulario tiene en pantalla (combobox o texto). Sin un país válido no
        // hay carril que exigir: el error ya cae en Country. Que la fila exista y sea del tenant
        // lo resuelve el handler contra ICompanyGeographyLookup. Guid.Empty cuenta como ausente:
        // NotEmpty() en un Guid? sólo rechaza null.
        RuleFor(command => command.CityId)
            .Must(cityId => cityId is { } value && value != Guid.Empty)
            .WithMessage("The city is required.")
            .When(command => HasValidCountry(command) && IsColombian(command));
        RuleFor(command => command.CityName)
            .NotEmpty()
            .WithMessage("The city is required.")
            .When(command => HasValidCountry(command) && !IsColombian(command));
        RuleFor(command => command.CityName)
            .MaximumLength(Company.CityNameMaxLength)
            .When(command => !string.IsNullOrWhiteSpace(command.CityName));

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

            // A catalogue code (spec D6), not just three letters: the account currency decides the
            // currency of every quotation billed to it, and a currency the catalogue lacks can
            // never be quoted.
            account.RuleFor(value => value.Currency)
                .Must(Currencies.IsSupported)
                .WithMessage("The currency must be one of the catalogue currencies.");
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

    /// <summary>
    /// El país recortado y en mayúsculas. Los handlers lo aplican al comando antes de validar, así
    /// validador, handler y dominio comparan contra el mismo valor: "co" y " CO" son Colombia para
    /// los tres, y la búsqueda de ciudad del handler no se salta por la caja.
    /// </summary>
    internal static string? NormalizeCountry(string? country) =>
        country?.Trim().ToUpperInvariant();

    // Recortado y en mayúsculas también aquí: el validador no depende de que el llamador haya
    // normalizado.
    private static string EffectiveCountry(ICompanyWriteCommand command) =>
        NormalizeCountry(command.Country) ?? string.Empty;

    private static bool IsColombian(ICompanyWriteCommand command) =>
        EffectiveCountry(command) == Company.ColombiaCountryCode;

    private static bool HasValidCountry(ICompanyWriteCommand command) =>
        command.Country is not null && IsValidCountry(command.Country);

    // Se recorta antes de medir, igual que el dominio: " es " llega bien formado.
    private static bool IsValidCountry(string country) =>
        country.Trim().Length == Company.CountryLength && country.Trim().All(char.IsAsciiLetter);

    private static bool HasNoDuplicates(IReadOnlyList<CompanyBankAccountPayload> accounts)
    {
        var keys = accounts.Select(account => (
            Bank: account.BankName?.Trim().ToLowerInvariant() ?? string.Empty,
            Number: account.AccountNumber?.Trim() ?? string.Empty,
            Currency: account.Currency?.Trim().ToUpperInvariant() ?? string.Empty));

        return keys.Distinct().Count() == accounts.Count;
    }
}
