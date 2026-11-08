using BuildingBlocks.Application;
using Modules.Buyers.Domain;

namespace Modules.Buyers.Application;

/// <param name="StoreName">El nombre que ve el comprador en el correo.</param>
/// <param name="PasswordHash">Ya calculado: quien llama lo hashea antes de mirar si el email existe, para que el tiempo no lo delate.</param>
/// <param name="Profile">Opcional. En una cuenta sin verificar, <c>null</c> borra el perfil que hubiera.</param>
public sealed record BuyerSignupRequest(
    string Host,
    string StoreName,
    string VisitorIp,
    string Email,
    string PasswordHash,
    BuyerProfileDto? Profile);

/// <summary>
/// El único algoritmo de alta de cuenta (registro y checkout). Nunca revela si el email existía: los tres caminos terminan igual
/// para quien llama. Email nuevo crea la cuenta; verificado sólo avisa que ya tiene una; sin verificar reemplaza contraseña y perfil
/// y reenvía la verificación. Si dos altas del mismo email nuevo chocan en el índice único, la perdedora descarta lo pendiente y
/// reintenta una vez por el camino de email existente.
/// </summary>
public sealed class BuyerSignup(
    IBuyerAccountRepository accounts,
    IBuyerConsentRepository consents,
    BuyerProfileWriter profiles,
    BuyerEmailTokenIssuer tokens,
    IBuyerOutbox outbox,
    IBuyersUnitOfWork unitOfWork,
    IClock clock)
{
    /// <param name="beforeSave">
    /// Corre justo antes de cada guardado, en el mismo <c>SaveChanges</c> que el efecto: el worker del checkout agrega ahí su marca de
    /// procesado y el cierre del inbox. Corre de nuevo en el reintento de la carrera, porque el descarte también se lleva lo suyo.
    /// </param>
    public async Task SignUpAsync(BuyerSignupRequest request, CancellationToken cancellationToken, Action? beforeSave = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!BuyerEmail.TryNormalize(request.Email, out var email))
        {
            throw new ArgumentException("The email must be valid and already validated.", nameof(request));
        }

        try
        {
            await SignUpOnceAsync(request, email, beforeSave, cancellationToken);
        }
        catch (ResourceConflictException exception) when (exception.Code == BuyerErrorCodes.EmailTaken)
        {
            // El ganador de la carrera ya guardó la cuenta: ahora existe, así que el segundo intento no vuelve a crearla.
            unitOfWork.DiscardPendingChanges();
            await SignUpOnceAsync(request, email, beforeSave, cancellationToken);
        }
    }

    private async Task SignUpOnceAsync(
        BuyerSignupRequest request, string email, Action? beforeSave, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var account = await accounts.FindByEmailAsync(email, cancellationToken);
        if (account is null)
        {
            account = BuyerAccount.Register(Guid.CreateVersion7(), email, request.PasswordHash, now);
            accounts.Add(account);
            if (request.Profile is not null)
            {
                await profiles.UpsertAsync(account.Id, request.Profile, now, cancellationToken);
            }
        }
        else if (account.IsVerified)
        {
            outbox.Add(
                BuyerIntegrationEvents.ExistingAccountNotice,
                new BuyerEmailPayload(account.Id, account.Email, request.Host, request.StoreName, null),
                now);
            beforeSave?.Invoke();
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return;
        }
        else
        {
            account.ReplacePendingRegistration(request.PasswordHash, now);
            if (request.Profile is null)
            {
                await profiles.ClearAsync(account.Id, cancellationToken);
            }
            else
            {
                await profiles.UpsertAsync(account.Id, request.Profile, now, cancellationToken);
            }
        }

        consents.Add(BuyerConsent.Record(Guid.CreateVersion7(), account.Id, email, request.VisitorIp, request.Host, now));
        await tokens.IssueAsync(account, BuyerEmailTokenPurpose.Verify, request.Host, request.StoreName, now, cancellationToken);
        beforeSave?.Invoke();
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
