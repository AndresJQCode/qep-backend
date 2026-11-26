using Modules.Buyers.Domain;

namespace Modules.Buyers.Application;

/// <summary>
/// Emite un token de email de la cuenta y encola el correo que lo lleva, todo en la unidad de trabajo del llamador: invalida los
/// tokens sin usar del mismo propósito (emitir uno nuevo anula los anteriores), guarda el hash del nuevo y escribe el evento del
/// outbox con el valor en claro. Lo comparten registro, reenvío y olvidé mi contraseña.
/// </summary>
public sealed class BuyerEmailTokenIssuer(IBuyerEmailTokenRepository tokens, IBuyerOutbox outbox)
{
    public async Task IssueAsync(
        BuyerAccount account,
        BuyerEmailTokenPurpose purpose,
        string storeHost,
        string storeName,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);

        await tokens.InvalidateUnusedAsync(account.Id, purpose, now, cancellationToken);
        var raw = BuyerTokenCodec.Generate();
        tokens.Add(BuyerEmailToken.Issue(Guid.CreateVersion7(), account.Id, purpose, BuyerTokenCodec.Hash(raw), now));
        outbox.Add(
            EventFor(purpose),
            new BuyerEmailPayload(account.Id, account.Email, storeHost, storeName, raw),
            now);
    }

    private static string EventFor(BuyerEmailTokenPurpose purpose) => purpose switch
    {
        BuyerEmailTokenPurpose.Verify => BuyerIntegrationEvents.EmailVerificationRequested,
        BuyerEmailTokenPurpose.Reset => BuyerIntegrationEvents.PasswordResetRequested,
        _ => throw new ArgumentOutOfRangeException(nameof(purpose), purpose, "Unknown email token purpose."),
    };
}
