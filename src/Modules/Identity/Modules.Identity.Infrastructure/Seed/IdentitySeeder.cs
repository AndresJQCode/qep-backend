using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Identity.Domain;
using Modules.Identity.Infrastructure.Persistence;

namespace Modules.Identity.Infrastructure.Seed;

/// <summary>
/// La mitad de Identity de la semilla de arranque.
/// </summary>
public static class IdentitySeeder
{
    /// <summary>
    /// Crea el usuario por email si no existe y devuelve su id. **No vincula ningún proveedor**:
    /// el `sub` de Google no se conoce hasta el primer login, y no hace falta —
    /// <c>ProviderLinkingService.LinkAndActivateAsync</c> busca por email verificado y lo
    /// vincula ahí. Un usuario sembrado sin proveedor es justo lo que esa ruta espera.
    /// </summary>
    public static async Task<Guid> SeedUserAsync(
        this IServiceProvider services,
        string email,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();

        // Se busca por email normalizado para que la semilla sea idempotente entre arranques,
        // sin importar la capitalización con la que se haya configurado Seed:OwnerEmail.
        var normalizedEmail = User.NormalizeEmail(email);
        var existing = await dbContext.Users.SingleOrDefaultAsync(
            user => user.Email == normalizedEmail, cancellationToken);
        if (existing is not null)
        {
            return existing.Id.Value;
        }

        var created = User.CreateInvited(UserId.New(), normalizedEmail, DateTimeOffset.UtcNow);
        dbContext.Users.Add(created);
        await dbContext.SaveChangesAsync(cancellationToken);
        return created.Id.Value;
    }
}
