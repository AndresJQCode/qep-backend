using BuildingBlocks.Domain.Identification;
using FluentValidation;
using Modules.Wholesale.Domain;

namespace Modules.Wholesale.Application;

/// <summary>
/// Los topes de texto de un distribuidor. Son los de <c>Customer</c> y <c>CustomerIdentification</c>: el distribuidor termina
/// siendo un cliente del mayorista (D-7) con esa razón social y ese documento. Las columnas de <c>wholesale.distributors</c>
/// usan los mismos.
/// </summary>
public static class DistributorTextLimits
{
    public const int BusinessName = 160;

    public const int IdentificationNumber = 32;

    public const int ContactName = 160;

    public const int Email = 254;

    /// <summary>Spec 2026-11-18: el teléfono como lo escribe el mayorista, recortado, antes de normalizar a E.164.</summary>
    public const int Phone = 32;

    /// <summary>El tope de <c>Company.CityNameMaxLength</c>: de la ciudad escrita nace la empresa del distribuidor.</summary>
    public const int CityName = Distributor.CityNameMaxLength;

    /// <summary>El tope de <c>CompanyContactInfo.AddressMaxLength</c>.</summary>
    public const int Address = Distributor.AddressMaxLength;
}

public sealed class InviteDistributorValidator : AbstractValidator<InviteDistributorCommand>
{
    public InviteDistributorValidator()
    {
        Include(new DistributorProfileValidator());
        RuleFor(command => command.PriceListId).NotEqual(Guid.Empty);
        RuleFor(command => command.AdvisorId).NotEqual(Guid.Empty);
        RuleFor(command => command.BillingCompanyId).NotEqual(Guid.Empty);
        RuleFor(command => command.Phone).NoticePhone();
        RuleFor(command => command.WhatsAppConnectionId)
            .NotEqual(Guid.Empty)
            .When(command => !string.IsNullOrWhiteSpace(command.Phone));
    }
}

/// <summary>
/// Las reglas de los datos del distribuidor, las mismas al invitar y al editarlo invitado. <c>internal</c> para que el registro
/// por ensamblado no la inscriba como un validador más. <paramref name="demandCity"/> es <c>false</c> sólo al editar una
/// invitación anterior a la ciudad sin tocar país ni ciudad: exigírsela dejaría sin editar a quien no la manda.
/// </summary>
internal sealed class DistributorProfileValidator : AbstractValidator<IDistributorProfile>
{
    public DistributorProfileValidator(bool demandCity = true)
    {
        RuleFor(command => command.Email)
            .Must(email => !string.IsNullOrWhiteSpace(email))
            .WithMessage("The email is required.")
            .Must(email => email is null || email.Trim().Length <= DistributorTextLimits.Email)
            .WithMessage($"The email has at most {DistributorTextLimits.Email} characters.")
            .Must(email => string.IsNullOrWhiteSpace(email) || IsEmail(email.Trim()))
            .WithMessage("The email is not valid.");
        RuleFor(command => command.BusinessName).TrimmedText(DistributorTextLimits.BusinessName);
        RuleFor(command => command.Country)
            .Must(IsCountryCode)
            .WithMessage("The country is required, as a two-letter code.");
        // El país mal escrito ya falla en su propia regla: el tipo sólo se juzga contra un país que tiene forma de país.
        RuleFor(command => command.IdentificationType)
            .NotEmpty()
            .WithMessage("The identification type is required.");
        RuleFor(command => command.IdentificationType)
            .Must((command, type) => DocumentTypesByCountry.IsAllowed(type, command.Country))
            .When(command => IsCountryCode(command.Country) && !string.IsNullOrWhiteSpace(command.IdentificationType))
            .WithMessage("The identification type is not valid for the country.");
        RuleFor(command => command.IdentificationNumber).TrimmedText(DistributorTextLimits.IdentificationNumber);
        // NIT, CC y CE solo llevan dígitos. Corre con un tipo y un número presentes (que falten lo dicen las reglas de
        // arriba); marca IdentificationNumber para que el 422 lleve el mapa errors con la clave del input.
        RuleFor(command => command.IdentificationNumber)
            .Must((command, number) => DocumentNumberFormat.IsValidFor(command.IdentificationType, number))
            .WithMessage(DocumentNumberFormat.NumericTypesMessage)
            .When(command => !string.IsNullOrWhiteSpace(command.IdentificationType)
                && !string.IsNullOrWhiteSpace(command.IdentificationNumber));
        RuleFor(command => command.ContactName).TrimmedText(DistributorTextLimits.ContactName);

        // La ciudad se exige con las mismas reglas y los mismos nombres de propiedad que CompanyWriteRules (Companies): de ahí
        // nace la empresa del distribuidor al aceptar, y lo que Companies rechazaría ahí se rechaza acá, al invitar. Se replica
        // y no se incluye porque CompanyWriteRules es interna de Companies y Wholesale no la referencia: si cambian las reglas
        // de ciudad de una, hay que cambiar la otra. Sin un país válido no hay carril que exigir: el error ya cae en Country.
        RuleFor(command => command.CityId)
            .Must(cityId => cityId is { } value && value != Guid.Empty)
            .WithMessage("The city is required.")
            .When(command => demandCity && IsCountryCode(command.Country) && IsColombian(command.Country));
        RuleFor(command => command.CityName)
            .Must(cityName => !string.IsNullOrWhiteSpace(cityName))
            .WithMessage("The city is required.")
            .When(command => demandCity && IsCountryCode(command.Country) && !IsColombian(command.Country));
        RuleFor(command => command.CityName)
            .Must(cityName => cityName!.Trim().Length <= DistributorTextLimits.CityName)
            .WithMessage($"The city has at most {DistributorTextLimits.CityName} characters.")
            .When(command => !string.IsNullOrWhiteSpace(command.CityName));
        RuleFor(command => command.Address)
            .Must(address => address!.Trim().Length <= DistributorTextLimits.Address)
            .WithMessage($"The address has at most {DistributorTextLimits.Address} characters.")
            .When(command => !string.IsNullOrWhiteSpace(command.Address));
    }

    internal static bool IsColombian(string country) => string.Equals(country.Trim(), "CO", StringComparison.OrdinalIgnoreCase);

    private static bool IsCountryCode(string? country) =>
        country is not null && country.Trim() is { Length: 2 } code && code.All(char.IsAsciiLetter);

    // Lo mismo que EmailAddress() de FluentValidation (una @ que no está en los extremos), sin el chequeo que no recorta.
    private static bool IsEmail(string email)
    {
        var at = email.IndexOf('@', StringComparison.Ordinal);
        return at > 0 && at == email.LastIndexOf('@') && at < email.Length - 1 && !email.Any(char.IsWhiteSpace);
    }
}

public sealed class UpdateDistributorValidator : AbstractValidator<UpdateDistributorCommand>
{
    public UpdateDistributorValidator()
    {
        RuleFor(command => command.PriceListId).NotEqual(Guid.Empty);
        RuleFor(command => command.AdvisorId).NotEqual(Guid.Empty);
        RuleFor(command => command.BillingCompanyId).NotEqual(Guid.Empty);
        RuleFor(command => command.Phone).NoticePhone();
        RuleFor(command => command.WhatsAppConnectionId)
            .NotEqual(Guid.Empty)
            .When(command => !string.IsNullOrWhiteSpace(command.Phone));
    }
}

internal static class DistributorTextRules
{
    /// <summary>Opcional; si viene, a lo sumo <see cref="DistributorTextLimits.Phone"/> caracteres recortado. Que sea un número
    /// válido lo decide el handler, con el país del tenant.</summary>
    public static IRuleBuilderOptions<T, string?> NoticePhone<T>(this IRuleBuilder<T, string?> rule) =>
        rule
            .Must(phone => phone is null || phone.Trim().Length <= DistributorTextLimits.Phone)
            .WithMessage($"The phone has at most {DistributorTextLimits.Phone} characters.");

    public static IRuleBuilderOptions<T, string> TrimmedText<T>(this IRuleBuilder<T, string> rule, int maxLength) =>
        rule
            .Must(text => !string.IsNullOrWhiteSpace(text))
            .WithMessage("The field is required.")
            .Must(text => text is null || text.Trim().Length <= maxLength)
            .WithMessage($"The field has at most {maxLength} characters.");
}
