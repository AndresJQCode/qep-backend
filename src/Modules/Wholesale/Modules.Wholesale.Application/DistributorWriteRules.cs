using FluentValidation;

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
}

public sealed class InviteDistributorValidator : AbstractValidator<InviteDistributorCommand>
{
    public InviteDistributorValidator()
    {
        RuleFor(command => command.Email)
            .Must(email => !string.IsNullOrWhiteSpace(email))
            .WithMessage("The email is required.")
            .Must(email => email is null || email.Trim().Length <= DistributorTextLimits.Email)
            .WithMessage($"The email has at most {DistributorTextLimits.Email} characters.")
            .Must(email => string.IsNullOrWhiteSpace(email) || IsEmail(email.Trim()))
            .WithMessage("The email is not valid.");
        RuleFor(command => command.BusinessName).TrimmedText(DistributorTextLimits.BusinessName);
        RuleFor(command => command.IdentificationNumber).TrimmedText(DistributorTextLimits.IdentificationNumber);
        RuleFor(command => command.ContactName).TrimmedText(DistributorTextLimits.ContactName);
        RuleFor(command => command.PriceListId).NotEqual(Guid.Empty);
        RuleFor(command => command.AdvisorId).NotEqual(Guid.Empty);
        RuleFor(command => command.BillingCompanyId).NotEqual(Guid.Empty);
        RuleFor(command => command.Phone).NoticePhone();
        RuleFor(command => command.WhatsAppConnectionId)
            .NotEqual(Guid.Empty)
            .When(command => !string.IsNullOrWhiteSpace(command.Phone));
    }

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
