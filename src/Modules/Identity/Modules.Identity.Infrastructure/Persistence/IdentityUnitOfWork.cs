using Microsoft.EntityFrameworkCore;
using Modules.Identity.Application;
using Modules.Identity.Domain;
using Npgsql;

namespace Modules.Identity.Infrastructure.Persistence;

internal sealed class IdentityUnitOfWork(IdentityDbContext dbContext) : IIdentityUnitOfWork
{
    /// <summary>unique_violation de PostgreSQL.</summary>
    private const string UniqueViolation = "23505";

    /// <summary>
    /// Índice único del email de <c>identity.users</c>. Se reconoce por nombre, no sólo por el
    /// 23505: <c>provider_links</c> y los tokens tienen sus propios índices únicos y no son un
    /// email repetido.
    /// </summary>
    private const string UserEmailIndex = "IX_users_email";

    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException
            {
                SqlState: UniqueViolation,
                ConstraintName: UserEmailIndex,
            })
        {
            // Dos registros del mismo email nuevo a la vez: el índice decide el ganador. Sin limpiar
            // el tracker el User perdedor sigue Added y el siguiente SaveChanges del scope lo
            // reintenta (mismo criterio que CustomersUnitOfWork con el BSUID). Quien llama relee al
            // ganador y sigue por el camino de «email existente».
            dbContext.ChangeTracker.Clear();
            throw new IdentityDomainException(
                "identity.user.email_taken",
                "Another user already uses that email.");
        }
    }
}
