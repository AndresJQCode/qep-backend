using BuildingBlocks.Application;
using FluentValidation;
using Modules.Buyers.Domain;

namespace Modules.Buyers.Application;

public sealed record DeleteBuyerAccountBody(string Password);

public sealed class DeleteBuyerAccountBodyValidator : AbstractValidator<DeleteBuyerAccountBody>
{
    public DeleteBuyerAccountBodyValidator()
    {
        // No NotEmpty(): también rechaza el espacio en blanco, y una contraseña así es válida.
        RuleFor(body => body.Password).Must(password => !string.IsNullOrEmpty(password))
            .WithErrorCode("NotEmptyValidator")
            .WithMessage("'Password' must not be empty.");
    }
}

public sealed record DeleteBuyerAccountCommand(string Host, string? Token, DeleteBuyerAccountBody Body) : ICommand<bool>;

/// <summary>
/// Elimina la cuenta de la sesión. La contraseña mala es 422 <c>password_incorrect</c> (D12, nunca 401) y no borra nada. El borrado
/// arrastra perfil, sesiones y tokens por la cascada; el consentimiento no tiene clave foránea y se conserva. El aviso al outbox
/// lleva el correo y la tienda (D8) porque cuando Notifications lo envía la cuenta ya no existe, y sale en el mismo guardado.
/// </summary>
public sealed class DeleteBuyerAccountHandler(
    IBuyerStorefronts storefronts,
    IBuyerAccountRepository accounts,
    IBuyerPasswordHasher hasher,
    BuyerSessions sessions,
    IBuyerOutbox outbox,
    IBuyersUnitOfWork unitOfWork,
    IValidator<DeleteBuyerAccountBody> validator,
    IClock clock)
    : ICommandHandler<DeleteBuyerAccountCommand, bool>
{
    public async Task<bool> HandleAsync(DeleteBuyerAccountCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var storefront = await storefronts.ResolveAsync(command.Host, cancellationToken);
        var session = await sessions.RequireAsync(storefront.Host, command.Token, cancellationToken);
        var account = await accounts.FindAsync(session.AccountId, cancellationToken)
            ?? throw new RequestUnauthorizedException(BuyerErrorCodes.SessionInvalid, "The buyer session is not valid.");
        await validator.ValidateAndThrowAsync(command.Body, cancellationToken);

        if (hasher.Verify(account.PasswordHash, command.Body.Password) == BuyerPasswordCheck.Failed)
        {
            throw new BuyerPasswordIncorrectException(nameof(DeleteBuyerAccountBody.Password));
        }

        outbox.Add(
            BuyerIntegrationEvents.AccountDeleted,
            new BuyerEmailPayload(account.Id, account.Email, storefront.Host, storefront.Name, null),
            clock.UtcNow);
        accounts.Remove(account);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return true;
    }
}
