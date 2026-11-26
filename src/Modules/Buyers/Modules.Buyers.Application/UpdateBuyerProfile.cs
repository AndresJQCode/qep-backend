using BuildingBlocks.Application;
using FluentValidation;
using Modules.Buyers.Domain;

namespace Modules.Buyers.Application;

public sealed record UpdateBuyerProfileCommand(string Host, string? Token, BuyerProfileDto Body) : ICommand<BuyerDto>;

/// <summary>
/// Reemplaza el perfil guardado del comprador de la sesión. No toca el email de ingreso y no juzga el país ni la ciudad contra la
/// tienda: eso lo hace el pedido. Orden: host (404) → sesión (401) → cuerpo (422).
/// </summary>
public sealed class UpdateBuyerProfileHandler(
    IBuyerStorefronts storefronts,
    IBuyerAccountRepository accounts,
    IBuyerProfileRepository profiles,
    BuyerSessions sessions,
    BuyerProfileWriter writer,
    IBuyersUnitOfWork unitOfWork,
    IValidator<BuyerProfileDto> validator,
    IClock clock)
    : ICommandHandler<UpdateBuyerProfileCommand, BuyerDto>
{
    public async Task<BuyerDto> HandleAsync(UpdateBuyerProfileCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var storefront = await storefronts.ResolveAsync(command.Host, cancellationToken);
        var session = await sessions.RequireAsync(storefront.Host, command.Token, cancellationToken);
        var account = await accounts.FindAsync(session.AccountId, cancellationToken)
            ?? throw new RequestUnauthorizedException(BuyerErrorCodes.SessionInvalid, "The buyer session is not valid.");
        await validator.ValidateAndThrowAsync(command.Body, cancellationToken);

        await writer.UpsertAsync(account.Id, command.Body, clock.UtcNow, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await BuyerViews.LoadAsync(account, profiles, cancellationToken);
    }
}
