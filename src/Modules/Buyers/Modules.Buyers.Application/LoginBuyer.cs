using BuildingBlocks.Application;
using FluentValidation;
using Modules.Buyers.Domain;

namespace Modules.Buyers.Application;

public sealed record LoginBuyerBody(string Email, string Password);

/// <summary>
/// Sólo "no vacío", a propósito: una regla de largo o de formato le diría a un atacante cómo luce una credencial válida. El email mal
/// formado se trata como cuenta inexistente en el manejador.
/// </summary>
public sealed class LoginBuyerBodyValidator : AbstractValidator<LoginBuyerBody>
{
    public LoginBuyerBodyValidator()
    {
        RuleFor(body => body.Email).NotEmpty();
        // No NotEmpty(): también rechaza el espacio en blanco, y el registro acepta una contraseña así.
        RuleFor(body => body.Password).Must(password => !string.IsNullOrEmpty(password))
            .WithErrorCode("NotEmptyValidator")
            .WithMessage("'Password' must not be empty.");
    }
}

public sealed record LoginBuyerCommand(string Host, string? VisitorIp, LoginBuyerBody Body) : ICommand<BuyerSessionDto>;

/// <summary>
/// Ingreso. Email inexistente (o mal formado) y contraseña mala son el mismo 401, y en ambos corre el hash (contra uno ficticio si
/// no hay cuenta). Contraseña correcta sin email verificado es 403 y no reenvía nada (D9).
/// </summary>
public sealed class LoginBuyerHandler(
    IBuyerStorefronts storefronts,
    IBuyerAccountRepository accounts,
    IBuyerProfileRepository profiles,
    IBuyerPasswordHasher hasher,
    BuyerSessions sessions,
    IValidator<LoginBuyerBody> validator,
    IClock clock)
    : ICommandHandler<LoginBuyerCommand, BuyerSessionDto>
{
    public async Task<BuyerSessionDto> HandleAsync(LoginBuyerCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var storefront = await storefronts.ResolveAsync(command.Host, cancellationToken);
        await validator.ValidateAndThrowAsync(command.Body, cancellationToken);

        var account = BuyerEmail.TryNormalize(command.Body.Email.Trim(), out var email)
            ? await accounts.FindByEmailAsync(email, cancellationToken)
            : null;
        var check = hasher.Verify(account?.PasswordHash, command.Body.Password);
        if (account is null || check == BuyerPasswordCheck.Failed)
        {
            throw new RequestUnauthorizedException(BuyerErrorCodes.CredentialsInvalid, "The email or password is not correct.");
        }

        if (!account.IsVerified)
        {
            throw new RequestForbiddenException(BuyerErrorCodes.EmailNotVerified, "The email address has not been verified.");
        }

        if (check == BuyerPasswordCheck.SuccessRehashNeeded)
        {
            // Se guarda junto con la sesión (misma unidad de trabajo).
            account.ChangePasswordHash(hasher.Hash(command.Body.Password), clock.UtcNow);
        }

        var (token, expiresAt) = await sessions.IssueAsync(account.Id, storefront.Host, command.VisitorIp, cancellationToken);
        return new BuyerSessionDto(token, expiresAt, await BuyerViews.LoadAsync(account, profiles, cancellationToken));
    }
}

internal static class BuyerViews
{
    public static async Task<BuyerDto> LoadAsync(
        BuyerAccount account, IBuyerProfileRepository profiles, CancellationToken cancellationToken)
    {
        var profile = await profiles.FindAsync(account.Id, cancellationToken);
        return new BuyerDto(account.Email, profile?.Data.ToDto());
    }
}
