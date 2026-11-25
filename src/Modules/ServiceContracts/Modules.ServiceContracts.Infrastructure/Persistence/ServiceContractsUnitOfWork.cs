using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Modules.ServiceContracts.Application;
using Modules.ServiceContracts.Domain;
using Modules.ServiceContracts.Infrastructure.Reminders;

namespace Modules.ServiceContracts.Infrastructure.Persistence;

internal sealed class ServiceContractsUnitOfWork(ServiceContractsDbContext dbContext) : IServiceContractsUnitOfWork
{
    /// <summary>PK de customer_profiles (InitialBilling): el primer PUT del perfil y el primer servicio de un cliente sin perfil.</summary>
    private const string ProfilePrimaryKey = "PK_customer_profiles";

    /// <summary>PK de settings (InitialBilling): el primer PUT de los ajustes del tenant.</summary>
    private const string SettingsPrimaryKey = "PK_settings";

    /// <summary>PK de ipc_rates (AddPriceAdjustment): la primera tasa de un año.</summary>
    private const string IpcRatePrimaryKey = "PK_ipc_rates";

    /// <summary>Un cobro por contrato y período: la corrida generó ese período mientras la acción generaba el mismo.</summary>
    private const string ChargePeriodIndex = "IX_charges_service_period";

    /// <summary>Un programado pendiente por contrato: dos «programar» a la vez.</summary>
    private const string PendingScheduleIndex = "IX_service_price_schedules_pending";

    public void Clear() => dbContext.ChangeTracker.Clear();

    // Borrar clientes (spec 2026-11-10, P1/P2): guardar un servicio o un perfil de cobro es escribir una referencia a customers.customers, que no
    // tiene FK. CustomerReferenceWrites toma el candado compartido de cada cliente y, ya con él, comprueba que la fila exista,
    // todo dentro de la transacción que guarda: si el borrado ganó la carrera, el candado espera a su commit y la comprobación
    // ve el cliente ya borrado.
    private static readonly CustomerReferenceSource[] CustomerReferenceSources =
    [
        new(typeof(ServiceContract), nameof(ServiceContract.CustomerId), ServiceContractsErrorCodes.CustomerNotFound),
        // Final review (R7 revocado): PUT /customers/{id}/profile crea un perfil sin servicio, y el borrado lo borra (§4.3). Sin el
        // candado, un perfil creado durante el borrado quedaría huérfano.
        new(typeof(CustomerBillingProfile), nameof(CustomerBillingProfile.CustomerId), ServiceContractsErrorCodes.CustomerNotFound),
    ];

    private Task<int> SaveHoldingCustomerReferencesAsync(CancellationToken cancellationToken) =>
        CustomerReferenceWrites.SaveHoldingAsync(
            dbContext,
            CustomerReferenceWrites.Collect(dbContext.ChangeTracker, CustomerReferenceSources),
            reference => new ServiceContractsDomainException(
                reference.NotFoundCode, $"Customer '{reference.CustomerId}' was not found."),
            cancellationToken);

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await SaveHoldingCustomerReferencesAsync(cancellationToken);
        }
        // Traducido acá y no en Application, que no referencia EF Core.
        catch (DbUpdateConcurrencyException exception)
        {
            throw ConcurrencyConflict(exception);
        }
        // Una acción del usuario que libera y genera puede chocar con la corrida diaria en el período, y dos «programar» en el
        // pendiente: para quien llama es lo mismo que el token de versión, «cambió mientras tanto». La corrida no pasa por acá:
        // guarda con el DbContext y relee. Los demás índices del worker (IX_customer_messages_...) siguen sin traducir.
        catch (DbUpdateException exception) when (UniqueViolation.Is(exception, ChargePeriodIndex, PendingScheduleIndex))
        {
            throw ConcurrencyConflict(exception);
        }
        // Dos primeras escrituras a la vez (ambas vieron «no hay fila» y las dos insertan la misma clave): perdió la segunda. Es un
        // 409 reintentable —el reintento ya encuentra la fila y la actualiza—, no un 500. Se reconoce por nombre de PK
        // (precedente TenancyUnitOfWork) y no por 23505: los índices del worker son deduplicación que decide quien los usa.
        catch (DbUpdateException exception) when (UniqueViolation.Is(exception, ProfilePrimaryKey, SettingsPrimaryKey, IpcRatePrimaryKey))
        {
            throw new ResourceConflictException(
                "billing.concurrent_first_write",
                "Another request created the same record first. Retry the request.",
                exception);
        }
    }

    private static RequestConcurrencyException ConcurrencyConflict(Exception exception) =>
        new("concurrency.conflict", "The billing record changed while the update was being committed.", exception);
}
