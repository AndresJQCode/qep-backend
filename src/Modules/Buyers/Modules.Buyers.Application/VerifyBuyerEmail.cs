using BuildingBlocks.Application;
using FluentValidation;
using Modules.Buyers.Domain;

namespace Modules.Buyers.Application;

public sealed record VerifyBuyerEmailBody(string Token);

public sealed class VerifyBuyerEmailBodyValidator : AbstractValidator<VerifyBuyerEmailBody>
{
    public const int TokenMaxLength = 128;

    public VerifyBuyerEmailBodyValidator()
    {
        RuleFor(body => body.Token).NotEmpty().MaximumLength(TokenMaxLength);
    }
}

public sealed record VerifyBuyerEmailCommand(string Host, VerifyBuyerEmailBody Body) : ICommand<bool>;

/// <summary>
/// Consume un token Verify y marca la cuenta como verificada. Cualquier token Verify sin usar de la cuenta sirve (una carrera de
/// altas puede dejar dos), y un token inexistente, vencido, usado o de otro propósito es el mismo <c>token_invalid</c>. No abre sesión.
/// </summary>
public sealed class VerifyBuyerEmailHandler(
    IBuyerStorefronts storefronts,
    IBuyerEmailTokenRepository tokens,
    IBuyerAccountRepository accounts,
    IBuyersUnitOfWork unitOfWork,
    IValidator<VerifyBuyerEmailBody> validator,
    IClock clock)
    : ICommandHandler<VerifyBuyerEmailCommand, bool>
{
    public async Task<bool> HandleAsync(VerifyBuyerEmailCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        await storefronts.ResolveAsync(command.Host, cancellationToken);
        await validator.ValidateAndThrowAsync(command.Body, cancellationToken);

        var now = clock.UtcNow;
        var accountId = await tokens.TryConsumeAsync(
            BuyerTokenCodec.Hash(command.Body.Token.Trim()), BuyerEmailTokenPurpose.Verify, now, cancellationToken)
            ?? throw new BuyerDomainException(BuyerErrorCodes.TokenInvalid, "The verification link is not valid.");

        // El token ya se consumió: si un re-registro confirmó en el medio, se verifica la cuenta releída. Si la purga la borró,
        // el segundo intento no la encuentra y es token_invalid.
        return await BuyerConcurrency.RetryOnceAsync(unitOfWork, async () =>
        {
            var account = await accounts.FindAsync(accountId, cancellationToken)
                ?? throw new BuyerDomainException(BuyerErrorCodes.TokenInvalid, "The verification link is not valid.");
            account.MarkEmailVerified(now);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return true;
        });
    }
}

public sealed record ResendBuyerVerificationBody(string Email, string TurnstileToken);

public sealed class ResendBuyerVerificationBodyValidator : AbstractValidator<ResendBuyerVerificationBody>
{
    public ResendBuyerVerificationBodyValidator()
    {
        RuleFor(body => body.Email).NotEmpty().EmailAddress().MaximumLength(BuyerEmail.MaxLength);
        RuleFor(body => body.TurnstileToken).NotEmpty().MaximumLength(RegisterBuyerBodyValidator.TurnstileTokenMaxLength);
    }
}

public sealed record ResendBuyerVerificationCommand(string Host, string VisitorIp, ResendBuyerVerificationBody Body)
    : ICommand<bool>;

/// <summary>
/// Reenvía la verificación. Anti-enumeración: termina igual exista o no la cuenta, o esté ya verificada. El orden del contrato es
/// host (404) → cuerpo (422) → Turnstile → efecto. Sólo una cuenta existente sin verificar recibe un token nuevo (que invalida los
/// anteriores) y su correo.
/// </summary>
public sealed class ResendBuyerVerificationHandler(
    IBuyerStorefronts storefronts,
    IBuyerCaptcha captcha,
    IBuyerAccountRepository accounts,
    BuyerEmailTokenIssuer tokens,
    IBuyersUnitOfWork unitOfWork,
    IValidator<ResendBuyerVerificationBody> validator,
    IClock clock)
    : ICommandHandler<ResendBuyerVerificationCommand, bool>
{
    public async Task<bool> HandleAsync(ResendBuyerVerificationCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var storefront = await storefronts.ResolveAsync(command.Host, cancellationToken);
        var body = command.Body with { Email = command.Body.Email?.Trim() ?? string.Empty };
        await validator.ValidateAndThrowAsync(body, cancellationToken);

        if (!await captcha.VerifyAsync(body.TurnstileToken.Trim(), command.VisitorIp, storefront.Host, cancellationToken))
        {
            throw new BuyerDomainException(BuyerErrorCodes.CaptchaInvalid, "The captcha was not passed.");
        }

        if (!BuyerEmail.TryNormalize(body.Email, out var email))
        {
            return true;
        }

        // Escribe la cuenta (M4): si la purga o un restablecimiento confirmaron en el medio, se relee y sigue la regla de lo que hay
        // ahora (borrada o verificada = no se reenvía nada), siempre con la misma respuesta neutra.
        return await BuyerConcurrency.RetryOnceAsync(unitOfWork, async () =>
        {
            var account = await accounts.FindByEmailAsync(email, cancellationToken);
            if (account is null || account.IsVerified)
            {
                return true;
            }

            var now = clock.UtcNow;
            // M4: el reenvío reinicia los 7 días de la cuenta sin verificar; si no, un reenvío cerca del día 7 manda un enlace que
            // la purga mata. Va en el mismo guardado que el token.
            account.MarkVerificationResent(now);
            await tokens.IssueAsync(
                account, BuyerEmailTokenPurpose.Verify, storefront.Host, storefront.Name, now, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return true;
        });
    }
}
