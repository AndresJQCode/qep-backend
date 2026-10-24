using FluentValidation;
using Modules.Suppliers.Domain;
using Modules.Tenancy.Application;

namespace Modules.Suppliers.Application;

/// <summary>
/// Lo que un POST y un PUT de proveedor tienen en común, con propiedades **planas**: así las claves
/// del mapa <c>errors</c> son <c>Name</c> o <c>BankAccounts[2].Currency</c> y no <c>Body.Name</c>.
/// </summary>
public interface ISupplierWriteCommand
{
    string Name { get; }

    string? BusinessName { get; }

    string IdentificationType { get; }

    string IdentificationNumber { get; }

    string Country { get; }

    Guid? CityId { get; }

    string? CityName { get; }

    string? Phone { get; }

    string? Email { get; }

    string? Address { get; }

    int PaymentTermDays { get; }

    string? Category { get; }

    string? Notes { get; }

    IReadOnlyList<SupplierBankAccountPayload> BankAccounts { get; }

    IReadOnlyList<SupplierContactPayload> Contacts { get; }
}

/// <summary>
/// Las reglas de escritura de proveedor, escritas una sola vez y compartidas por <c>Include()</c> en
/// Create y Update. El dominio hace cumplir las mismas y lanzaría un 422 con un solo código; el
/// validador existe para que la respuesta lleve el mapa <c>errors</c> **por campo** (PascalCase,
/// con índice en las listas), que es lo único que el formulario sabe leer.
/// </summary>
internal sealed class SupplierWriteRules : AbstractValidator<ISupplierWriteCommand>
{
    public SupplierWriteRules()
    {
        RuleFor(command => command.Name)
            .NotEmpty()
            .MaximumLength(Supplier.NameMaxLength);
        RuleFor(command => command.BusinessName)
            .MaximumLength(Supplier.BusinessNameMaxLength);
        RuleFor(command => command.IdentificationNumber)
            .NotEmpty()
            .MaximumLength(SupplierIdentification.NumberMaxLength);

        // Contra la misma tabla que el dominio y no una lista repetida aquí: dos listas de valores
        // válidos que se amplían por separado terminan discrepando.
        RuleFor(command => command.IdentificationType)
            .NotEmpty()
            .Must(IsSupportedIdentificationType)
            .WithMessage(_ =>
                $"The identification type must be one of {string.Join(", ", IdentificationTypeParser.SupportedWireValues.Order(StringComparer.Ordinal))}.")
            .When(command => !string.IsNullOrWhiteSpace(command.IdentificationType));

        // Dos RuleFor por la misma razón de siempre: el When() gobierna toda la cadena que lo
        // precede, y un NotEmpty() delante del Length quedaría apagado justo con el país vacío.
        RuleFor(command => command.Country).NotEmpty();
        RuleFor(command => command.Country)
            .Length(SupplierContactInfo.CountryLength)
            .Must(country => country.All(char.IsAsciiLetter))
            .WithMessage(
                $"The country must be an ISO-3166-1 alpha-2 code ({SupplierContactInfo.CountryLength} letters).")
            .When(command => !string.IsNullOrWhiteSpace(command.Country));

        // El país decide cuál de los dos carriles de ciudad se exige, y el error tiene que caer en
        // el campo que el formulario tiene en pantalla (combobox o texto). Guid.Empty cuenta como
        // ausente: NotEmpty() en un Guid? sólo rechaza null.
        RuleFor(command => command.CityId)
            .Must(cityId => cityId is { } value && value != Guid.Empty)
            .WithMessage("The city is required.")
            .When(IsColombian);
        RuleFor(command => command.CityName)
            .NotEmpty()
            .WithMessage("The city is required.")
            .When(command => !IsColombian(command));
        RuleFor(command => command.CityName)
            .MaximumLength(Supplier.CityNameMaxLength)
            .When(command => !string.IsNullOrWhiteSpace(command.CityName));

        // Teléfono, correo y dirección son opcionales (spec D-4): vacío no es error.
        RuleFor(command => command.Phone)
            .MaximumLength(SupplierContactInfo.PhoneMaxLength);
        RuleFor(command => command.Address)
            .MaximumLength(SupplierContactInfo.AddressMaxLength);
        RuleFor(command => command.Email)
            .MaximumLength(SupplierContactInfo.EmailMaxLength)
            .EmailAddress()
            .When(command => !string.IsNullOrWhiteSpace(command.Email));

        RuleFor(command => command.PaymentTermDays)
            .InclusiveBetween(0, Supplier.PaymentTermMaxDays);
        RuleFor(command => command.Category)
            .MaximumLength(Supplier.CategoryMaxLength);
        RuleFor(command => command.Notes)
            .MaximumLength(Supplier.NotesMaxLength);

        // Las listas son obligatorias como lista (vacía vale): una ausente en el JSON llega como
        // null y sin esta regla saldría como 500 en el dominio.
        RuleFor(command => command.BankAccounts)
            .NotNull();
        RuleFor(command => command.BankAccounts)
            .Must(accounts => accounts.Count <= SupplierBankAccount.MaxPerSupplier)
            .WithMessage(
                $"A supplier cannot have more than {SupplierBankAccount.MaxPerSupplier} bank accounts.")
            .When(command => command.BankAccounts is not null);
        RuleForEach(command => command.BankAccounts).ChildRules(account =>
        {
            account.RuleFor(value => value.BankName)
                .NotEmpty()
                .MaximumLength(SupplierBankAccount.BankNameMaxLength);
            account.RuleFor(value => value.AccountNumber)
                .NotEmpty()
                .MaximumLength(SupplierBankAccount.AccountNumberMaxLength);

            // Un código del catálogo, no sólo tres letras: una moneda que el catálogo no tiene no
            // se puede usar en ningún documento.
            account.RuleFor(value => value.Currency)
                .Must(Currencies.IsSupported)
                .WithMessage("The currency must be one of the catalogue currencies.");
        });

        // El duplicado se comprueba aquí además del dominio, que lanza un código suelto sin mapa de
        // errores: el formulario no marcaría ninguna fila. Se ancla a la **segunda** aparición.
        RuleFor(command => command.BankAccounts)
            .Custom(AddDuplicatedAccountFailures)
            .When(command => command.BankAccounts is not null);

        RuleFor(command => command.Contacts)
            .NotNull();
        RuleFor(command => command.Contacts)
            .Must(contacts => contacts.Count <= SupplierContact.MaxPerSupplier)
            .WithMessage($"A supplier cannot have more than {SupplierContact.MaxPerSupplier} contacts.")
            .When(command => command.Contacts is not null);
        RuleForEach(command => command.Contacts).ChildRules(contact =>
        {
            contact.RuleFor(value => value.Name)
                .NotEmpty()
                .MaximumLength(SupplierContact.NameMaxLength);
            contact.RuleFor(value => value.Role)
                .MaximumLength(SupplierContact.RoleMaxLength);
            contact.RuleFor(value => value.Phone)
                .MaximumLength(SupplierContact.PhoneMaxLength);
            contact.RuleFor(value => value.Email)
                .MaximumLength(SupplierContact.EmailMaxLength)
                .EmailAddress()
                .When(value => !string.IsNullOrWhiteSpace(value.Email));
        });
    }

    // Un país ausente cuenta como Colombia a los efectos de **cuál** ciudad exigir: el país ya
    // tiene su propia regla NotEmpty, y apagar los dos carriles haría que un cuerpo sin país
    // tampoco reportara la ciudad.
    private static bool IsColombian(ISupplierWriteCommand command) =>
        string.IsNullOrWhiteSpace(command.Country)
        || string.Equals(
            command.Country.Trim(),
            Supplier.ColombiaCountryCode,
            StringComparison.OrdinalIgnoreCase);

    private static bool IsSupportedIdentificationType(string? value)
    {
        try
        {
            IdentificationTypeParser.Parse(value);
            return true;
        }
        catch (SuppliersDomainException)
        {
            return false;
        }
    }

    // La clave es la de SupplierBankAccount.DeduplicationKey: banco sin distinguir caja, número
    // distinguiéndola, moneda en mayúsculas. Si difiere, el validador deja pasar lo que el dominio
    // rechaza y el 422 sale sin mapa.
    private static void AddDuplicatedAccountFailures(
        IReadOnlyList<SupplierBankAccountPayload> accounts,
        ValidationContext<ISupplierWriteCommand> context)
    {
        var seen = new HashSet<(string, string, string)>();
        for (var index = 0; index < accounts.Count; index++)
        {
            var account = accounts[index];
            var key = (
                account.BankName?.Trim().ToLowerInvariant() ?? string.Empty,
                account.AccountNumber?.Trim() ?? string.Empty,
                account.Currency?.Trim().ToUpperInvariant() ?? string.Empty);

            if (!seen.Add(key))
            {
                context.AddFailure(
                    $"BankAccounts[{index}].AccountNumber",
                    "The same bank account is listed more than once.");
            }
        }
    }
}
