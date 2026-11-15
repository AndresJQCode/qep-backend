using BuildingBlocks.Application;
using FluentValidation;
using Microsoft.Extensions.Logging;
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
/// no hay cuenta). Contraseña correcta sin email verificado es 403 y no reenvía nada (D9). Ya con la sesión guardada, enlaza a la
/// cuenta los pedidos de la tienda de ese tenant hechos con su correo (spec 2026-11-15 §3.2).
/// </summary>
public sealed partial class LoginBuyerHandler(
    IBuyerStorefronts storefronts,
    IBuyerAccountRepository accounts,
    IBuyerProfileRepository profiles,
    IBuyerPasswordHasher hasher,
    BuyerSessions sessions,
    IBuyersUnitOfWork unitOfWork,
    IValidator<LoginBuyerBody> validator,
    IClock clock,
    IBuyerOrderLinker orderLinker,
    ILogger<LoginBuyerHandler> logger)
    : ICommandHandler<LoginBuyerCommand, BuyerSessionDto>
{
    public async Task<BuyerSessionDto> HandleAsync(LoginBuyerCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var storefront = await storefronts.ResolveAsync(command.Host, cancellationToken);
        await validator.ValidateAndThrowAsync(command.Body, cancellationToken);

        // Sólo el re-hash escribe la cuenta. Si choca (un restablecimiento o un cambio de contraseña confirmó en el medio), la sesión
        // y el re-hash se descartan juntos y el ingreso se intenta de nuevo contra el hash actual: con la contraseña vieja es 401.
        var login = await BuyerConcurrency.RetryOnceAsync(
            unitOfWork, () => AttemptAsync(command, storefront, cancellationToken));

        await LinkStoreOrdersAsync(storefront.TenantId, login.AccountId, login.Email, cancellationToken);
        return login.Session;
    }

    // Después del commit de la sesión y fuera de su transacción: el enlace nunca hace fallar el ingreso. Se registra sólo el id de la
    // cuenta y el tipo de la excepción, nunca el correo.
    private async Task LinkStoreOrdersAsync(Guid tenantId, Guid accountId, string email, CancellationToken cancellationToken)
    {
        try
        {
            await orderLinker.LinkByEmailAsync(tenantId, accountId, email, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogOrderLinkFailed(logger, accountId, exception.GetType().Name);
        }
    }

    private sealed record LoginResult(BuyerSessionDto Session, Guid AccountId, string Email);

    private async Task<LoginResult> AttemptAsync(
        LoginBuyerCommand command, BuyerStorefront storefront, CancellationToken cancellationToken)
    {
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
        var session = new BuyerSessionDto(token, expiresAt, await BuyerViews.LoadAsync(account, profiles, cancellationToken));
        return new LoginResult(session, account.Id, account.Email);
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Linking the store orders of buyer account {AccountId} on login failed with {ExceptionType}; the login stands.")]
    private static partial void LogOrderLinkFailed(ILogger logger, Guid accountId, string exceptionType);
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
