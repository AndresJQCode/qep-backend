using BuildingBlocks.Application;
using Modules.Buyers.Domain;

namespace Modules.Buyers.Application;

public sealed record GetBuyerSessionQuery(string Host, string? Token) : IQuery<BuyerDto>;

/// <summary>Lee al comprador de la sesión del host. Cualquier sesión que no sirve es <c>session_invalid</c>.</summary>
public sealed class GetBuyerSessionHandler(
    IBuyerStorefronts storefronts,
    IBuyerAccountRepository accounts,
    IBuyerProfileRepository profiles,
    BuyerSessions sessions)
    : IQueryHandler<GetBuyerSessionQuery, BuyerDto>
{
    public async Task<BuyerDto> HandleAsync(GetBuyerSessionQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var storefront = await storefronts.ResolveAsync(query.Host, cancellationToken);
        var session = await sessions.RequireAsync(storefront.Host, query.Token, cancellationToken);
        var account = await accounts.FindAsync(session.AccountId, cancellationToken)
            ?? throw new RequestUnauthorizedException(BuyerErrorCodes.SessionInvalid, "The buyer session is not valid.");
        return await BuyerViews.LoadAsync(account, profiles, cancellationToken);
    }
}

public sealed record LogoutBuyerCommand(string Host, string? Token) : ICommand<bool>;

/// <summary>Cierra la sesión. Idempotente: sin una sesión válida en ese host no hace nada y termina igual.</summary>
public sealed class LogoutBuyerHandler(
    IBuyerStorefronts storefronts,
    BuyerSessions sessions,
    IBuyersUnitOfWork unitOfWork,
    IClock clock)
    : ICommandHandler<LogoutBuyerCommand, bool>
{
    public async Task<bool> HandleAsync(LogoutBuyerCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var storefront = await storefronts.ResolveAsync(command.Host, cancellationToken);
        var session = await sessions.TryResolveAsync(storefront.Host, command.Token, cancellationToken);
        if (session is null)
        {
            return true;
        }

        session.Revoke(clock.UtcNow, BuyerSession.RevokedReasons.Logout);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return true;
    }
}
