using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Modules.Billing.Application;
using Modules.Billing.Infrastructure.Reminders;

namespace Modules.Billing.Infrastructure.Persistence;

internal sealed class BillingUnitOfWork(BillingDbContext dbContext) : IBillingUnitOfWork
{
    /// <summary>PK de customer_profiles (InitialBilling): el primer PUT del perfil y el primer servicio de un cliente sin perfil.</summary>
    private const string ProfilePrimaryKey = "PK_customer_profiles";

    /// <summary>PK de settings (InitialBilling): el primer PUT de los ajustes del tenant.</summary>
    private const string SettingsPrimaryKey = "PK_settings";

    public void Clear() => dbContext.ChangeTracker.Clear();

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        // Traducido acá y no en Application, que no referencia EF Core. Las violaciones de los
        // índices únicos (IX_charges_service_due, IX_customer_messages_...) no se traducen: son la
        // deduplicación del worker, y quien la usa decide qué hacer al perderla.
        catch (DbUpdateConcurrencyException exception)
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict",
                "The billing record changed while the update was being committed.",
                exception);
        }
        // Dos primeras escrituras a la vez (ambas vieron «no hay fila» y las dos insertan la misma clave): perdió la segunda. Es un
        // 409 reintentable —el reintento ya encuentra la fila y la actualiza—, no un 500. Se reconoce por nombre de PK
        // (precedente TenancyUnitOfWork) y no por 23505: los índices del worker son deduplicación que decide quien los usa.
        catch (DbUpdateException exception) when (UniqueViolation.Is(exception, ProfilePrimaryKey, SettingsPrimaryKey))
        {
            throw new ResourceConflictException(
                "billing.concurrent_first_write",
                "Another request created the same record first. Retry the request.",
                exception);
        }
    }
}
