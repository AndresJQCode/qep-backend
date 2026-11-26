using BuildingBlocks.Application;
using FluentValidation;
using Modules.Buyers.Domain;

namespace Modules.Buyers.Application;

public sealed record ForgotBuyerPasswordBody(string Email, string TurnstileToken);

public sealed class ForgotBuyerPasswordBodyValidator : AbstractValidator<ForgotBuyerPasswordBody>
{
    public ForgotBuyerPasswordBodyValidator()
    {
        RuleFor(body => body.Email).NotEmpty().EmailAddress().MaximumLength(BuyerEmail.MaxLength);
        RuleFor(body => body.TurnstileToken).NotEmpty().MaximumLength(RegisterBuyerBodyValidator.TurnstileTokenMaxLength);
    }
}

public sealed record ForgotBuyerPasswordCommand(string Host, string VisitorIp, ForgotBuyerPasswordBody Body) : ICommand<bool>;

/// <summary>
/// Olvidé mi contraseña. Anti-enumeración: termina igual exista o no la cuenta, esté o no verificada. Orden del contrato: host
/// (404) → cuerpo (422) → Turnstile → efecto; un rechazo no escribe nada. El token Reset sale por el emisor compartido, que
/// invalida los Reset anteriores.
/// </summary>
public sealed class ForgotBuyerPasswordHandler(
    IBuyerStorefronts storefronts,
    IBuyerCaptcha captcha,
    IBuyerAccountRepository accounts,
    BuyerEmailTokenIssuer tokens,
    IBuyersUnitOfWork unitOfWork,
    IValidator<ForgotBuyerPasswordBody> validator,
    IClock clock)
    : ICommandHandler<ForgotBuyerPasswordCommand, bool>
{
    public async Task<bool> HandleAsync(ForgotBuyerPasswordCommand command, CancellationToken cancellationToken)
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

        var account = await accounts.FindByEmailAsync(email, cancellationToken);
        if (account is null)
        {
            return true;
        }

        await tokens.IssueAsync(
            account, BuyerEmailTokenPurpose.Reset, storefront.Host, storefront.Name, clock.UtcNow, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return true;
    }
}

public sealed record ResetBuyerPasswordBody(string Token, string NewPassword);

public sealed class ResetBuyerPasswordBodyValidator : AbstractValidator<ResetBuyerPasswordBody>
{
    public ResetBuyerPasswordBodyValidator()
    {
        RuleFor(body => body.Token).NotEmpty().MaximumLength(VerifyBuyerEmailBodyValidator.TokenMaxLength);
        RuleFor(body => body.NewPassword).BuyerPassword();
    }
}

public sealed record ResetBuyerPasswordCommand(string Host, ResetBuyerPasswordBody Body) : ICommand<bool>;

/// <summary>
/// Restablece la contraseña con un token Reset. El cuerpo se valida antes de consumir, así una contraseña inválida no quema el
/// token. Consumir es atómico (<c>TryConsumeAsync</c>); un token inexistente, vencido, usado o de otro propósito es el mismo
/// <c>token_invalid</c>. Cambia el hash, marca el email verificado (restablecer prueba el control del correo) y revoca todas las
/// sesiones, en una sola transacción. No abre sesión.
/// </summary>
public sealed class ResetBuyerPasswordHandler(
    IBuyerStorefronts storefronts,
    IBuyerEmailTokenRepository tokens,
    IBuyerAccountRepository accounts,
    IBuyerPasswordHasher hasher,
    BuyerSessions sessions,
    IBuyersUnitOfWork unitOfWork,
    IValidator<ResetBuyerPasswordBody> validator,
    IClock clock)
    : ICommandHandler<ResetBuyerPasswordCommand, bool>
{
    public async Task<bool> HandleAsync(ResetBuyerPasswordCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        await storefronts.ResolveAsync(command.Host, cancellationToken);
        await validator.ValidateAndThrowAsync(command.Body, cancellationToken);

        var now = clock.UtcNow;
        var accountId = await tokens.TryConsumeAsync(
            BuyerTokenCodec.Hash(command.Body.Token.Trim()), BuyerEmailTokenPurpose.Reset, now, cancellationToken)
            ?? throw new BuyerDomainException(BuyerErrorCodes.TokenInvalid, "The password reset link is not valid.");
        var hash = hasher.Hash(command.Body.NewPassword);

        // El token ya se consumió: si otra escritura (un re-registro, un re-hash del login) confirmó en el medio, el
        // restablecimiento se aplica de nuevo sobre la cuenta releída en vez de perderse.
        return await BuyerConcurrency.RetryOnceAsync(unitOfWork, async () =>
        {
            var account = await accounts.FindAsync(accountId, cancellationToken)
                ?? throw new BuyerDomainException(BuyerErrorCodes.TokenInvalid, "The password reset link is not valid.");
            account.ChangePasswordHash(hash, now);
            account.MarkEmailVerified(now);
            // RevokeAllAsync guarda: el hash nuevo, la verificación y las revocaciones salen en la misma transacción.
            await sessions.RevokeAllAsync(account.Id, BuyerSession.RevokedReasons.PasswordReset, cancellationToken);
            return true;
        });
    }
}
