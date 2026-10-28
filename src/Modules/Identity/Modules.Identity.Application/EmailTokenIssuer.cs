using BuildingBlocks.Application;
using Modules.Identity.Domain;

namespace Modules.Identity.Application;

public interface IEmailTokenIssuer
{
    /// <summary>
    /// Emite un token nuevo y devuelve el valor crudo, que el llamador manda por email y nunca se
    /// persiste. No guarda: el token entra en la unidad de trabajo del llamador.
    /// </summary>
    Task<string> IssueAsync(Guid userId, EmailTokenPurpose purpose, CancellationToken cancellationToken);
}

public sealed class EmailTokenIssuer(IEmailTokenRepository tokens, IClock clock) : IEmailTokenIssuer
{
    public async Task<string> IssueAsync(
        Guid userId,
        EmailTokenPurpose purpose,
        CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        await tokens.InvalidateUnusedAsync(userId, purpose, now, cancellationToken);

        var raw = EmailTokenCodec.Generate();
        tokens.Add(EmailToken.Issue(
            EmailTokenId.New(),
            new UserId(userId),
            purpose,
            EmailTokenCodec.Hash(raw),
            now));
        return raw;
    }
}