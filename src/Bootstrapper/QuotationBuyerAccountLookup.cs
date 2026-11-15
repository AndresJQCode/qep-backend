using Modules.Buyers.Application;
using Modules.Buyers.Domain;
using Modules.Quotations.Application;

namespace Bootstrapper;

/// <summary>Buyers → Quotations (spec 2026-11-15 §4): la cuenta de comprador que el detalle del pedido muestra y la que el
/// asesor elige por correo, sin que Quotations vea Buyers.</summary>
internal sealed class QuotationBuyerAccountLookup(IBuyerAccountRepository accounts) : IQuotationBuyerAccountLookup
{
    public async Task<QuotationBuyerAccountRef?> FindAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var account = await accounts.FindAsync(accountId, cancellationToken);
        return account is null ? null : new QuotationBuyerAccountRef(account.Id, account.Email);
    }

    // Como StoreBuyerAccounts.FindVerifiedAccountIdAsync: normaliza igual que Buyers y sólo responde la verificada.
    public async Task<QuotationBuyerAccountRef?> FindVerifiedByEmailAsync(string email, CancellationToken cancellationToken)
    {
        if (!BuyerEmail.TryNormalize(email, out var normalized))
        {
            return null;
        }

        var account = await accounts.FindByEmailAsync(normalized, cancellationToken);
        return account is { IsVerified: true } ? new QuotationBuyerAccountRef(account.Id, account.Email) : null;
    }
}
