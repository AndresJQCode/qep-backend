using BuildingBlocks.Application;
using FluentValidation;
using Modules.Buyers.Domain;

namespace Modules.Buyers.Application;

public sealed record RegisterBuyerBody(
    string Email,
    string Password,
    bool AcceptsDataPolicy,
    string TurnstileToken,
    BuyerProfileDto? Profile);

public sealed class RegisterBuyerBodyValidator : AbstractValidator<RegisterBuyerBody>
{
    public const int TurnstileTokenMaxLength = 2048;

    public RegisterBuyerBodyValidator()
    {
        RuleFor(body => body.Email).NotEmpty().EmailAddress().MaximumLength(BuyerEmail.MaxLength);
        RuleFor(body => body.Password).BuyerPassword();
        RuleFor(body => body.TurnstileToken).NotEmpty().MaximumLength(TurnstileTokenMaxLength);
        RuleFor(body => body.Profile!).SetValidator(new BuyerProfileValidator()).When(body => body.Profile is not null);
    }
}

public sealed record RegisterBuyerCommand(string Host, string VisitorIp, RegisterBuyerBody Body) : ICommand<bool>;

/// <summary>
/// El orden del contrato: host (404) → cuerpo (422) → consentimiento → Turnstile → efecto. Un rechazo no escribe nada. El hash corre
/// siempre, antes de mirar si el email existe, para que los tres caminos cuesten lo mismo.
/// </summary>
public sealed class RegisterBuyerHandler(
    IBuyerStorefronts storefronts,
    IBuyerCaptcha captcha,
    IBuyerPasswordHasher hasher,
    BuyerSignup signup,
    IValidator<RegisterBuyerBody> validator)
    : ICommandHandler<RegisterBuyerCommand, bool>
{
    public async Task<bool> HandleAsync(RegisterBuyerCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var storefront = await storefronts.ResolveAsync(command.Host, cancellationToken);

        // El espacio de los bordes no es parte del email: se quita antes de validar, igual que el resto de entradas.
        var body = command.Body with { Email = command.Body.Email?.Trim() ?? string.Empty };
        await validator.ValidateAndThrowAsync(body, cancellationToken);
        if (!body.AcceptsDataPolicy)
        {
            throw new BuyerDomainException(BuyerErrorCodes.ConsentRequired, "The data policy must be accepted.");
        }

        if (!await captcha.VerifyAsync(body.TurnstileToken.Trim(), command.VisitorIp, storefront.Host, cancellationToken))
        {
            throw new BuyerDomainException(BuyerErrorCodes.CaptchaInvalid, "The captcha was not passed.");
        }

        var passwordHash = hasher.Hash(body.Password);
        await signup.SignUpAsync(
            new BuyerSignupRequest(
                storefront.Host, storefront.Name, command.VisitorIp, body.Email, passwordHash, body.Profile),
            cancellationToken);
        return true;
    }
}
