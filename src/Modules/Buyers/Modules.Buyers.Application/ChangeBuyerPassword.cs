using BuildingBlocks.Application;
using FluentValidation;
using Modules.Buyers.Domain;

namespace Modules.Buyers.Application;

public sealed record ChangeBuyerPasswordBody(string CurrentPassword, string NewPassword);

public sealed class ChangeBuyerPasswordBodyValidator : AbstractValidator<ChangeBuyerPasswordBody>
{
    public ChangeBuyerPasswordBodyValidator()
    {
        // No NotEmpty(): también rechaza el espacio en blanco, y una contraseña así es válida.
        RuleFor(body => body.CurrentPassword).Must(password => !string.IsNullOrEmpty(password))
            .WithErrorCode("NotEmptyValidator")
            .WithMessage("'Current Password' must not be empty.");
        RuleFor(body => body.NewPassword).BuyerPassword();
    }
}

public sealed record ChangeBuyerPasswordCommand(string Host, string? Token, ChangeBuyerPasswordBody Body) : ICommand<bool>;

/// <summary>
/// Cambia la contraseña de la sesión. La actual mala es 422 <c>password_incorrect</c> (D12, nunca 401). El cambio y la revocación de
/// las demás sesiones se guardan juntos; la sesión desde la que se cambia sigue valiendo. Orden: host → sesión → cuerpo → verificación.
/// </summary>
public sealed class ChangeBuyerPasswordHandler(
    IBuyerStorefronts storefronts,
    IBuyerAccountRepository accounts,
    IBuyerPasswordHasher hasher,
    BuyerSessions sessions,
    IBuyersUnitOfWork unitOfWork,
    IValidator<ChangeBuyerPasswordBody> validator,
    IClock clock)
    : ICommandHandler<ChangeBuyerPasswordCommand, bool>
{
    public async Task<bool> HandleAsync(ChangeBuyerPasswordCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var storefront = await storefronts.ResolveAsync(command.Host, cancellationToken);
        // Si otra escritura de la cuenta (un restablecimiento) confirmó en el medio, se relee todo: la sesión puede haber quedado
        // revocada (401) y la contraseña actual puede ya no ser la misma (422).
        return await BuyerConcurrency.RetryOnceAsync(
            unitOfWork, () => AttemptAsync(command, storefront, cancellationToken));
    }

    private async Task<bool> AttemptAsync(
        ChangeBuyerPasswordCommand command, BuyerStorefront storefront, CancellationToken cancellationToken)
    {
        var session = await sessions.RequireAsync(storefront.Host, command.Token, cancellationToken);
        var account = await accounts.FindAsync(session.AccountId, cancellationToken)
            ?? throw new RequestUnauthorizedException(BuyerErrorCodes.SessionInvalid, "The buyer session is not valid.");
        await validator.ValidateAndThrowAsync(command.Body, cancellationToken);

        if (hasher.Verify(account.PasswordHash, command.Body.CurrentPassword) == BuyerPasswordCheck.Failed)
        {
            throw new BuyerPasswordIncorrectException(nameof(ChangeBuyerPasswordBody.CurrentPassword));
        }

        account.ChangePasswordHash(hasher.Hash(command.Body.NewPassword), clock.UtcNow);
        // RevokeOthersAsync guarda: el hash nuevo y las revocaciones salen en la misma transacción.
        await sessions.RevokeOthersAsync(
            account.Id, session.Id, BuyerSession.RevokedReasons.PasswordChanged, cancellationToken);
        return true;
    }
}
