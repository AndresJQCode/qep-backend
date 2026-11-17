using Microsoft.EntityFrameworkCore;
using Modules.Customers.Application;
using Modules.Customers.Domain;

namespace Modules.Customers.Infrastructure.Persistence;

/// <summary>
/// Spec 2026-11-10 (borrar clientes) §4.3, A-5 y A-6. Sentencias directas sobre el <see cref="CustomersDbContext"/> del scope:
/// con la transacción de <see cref="CustomersUnitOfWork.BeginTransactionAsync"/> abierta usan su conexión y quedan dentro de
/// ella. Cada una va por lote, nunca por cliente. Direcciones, notas y etiquetas se van por el <c>CASCADE</c> de sus FK;
/// <c>data_consents</c> no tiene FK (A-5) y se queda con la foto. Como en <see cref="CustomerMergeStore"/>, lo único que se
/// traduce acá es la carrera (<see cref="CustomersWriteRaces"/>), porque estas sentencias no pasan por el unit of work.
/// </summary>
internal sealed class CustomerDeletionStore(CustomersDbContext dbContext) : ICustomerDeletionStore
{
    public async Task<IReadOnlyList<Customer>> FindDeletableAsync(
        Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        var customerIds = ToCustomerIds(ids);
        if (customerIds.Length == 0)
        {
            return [];
        }

        // Sin tracking: el borrado no pasa por el agregado, y una entidad rastreada que después desaparece con ExecuteDelete sólo
        // confundiría al SaveChanges del handler.
        return await dbContext.Customers
            .AsNoTracking()
            .Where(customer => customer.TenantId == tenantId && customerIds.Contains(customer.Id) && customer.MergedIntoId == null)
            .ToListAsync(cancellationToken);
    }

    public async Task LockRowsAsync(Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        Guid[] raw = [.. ids.Distinct().Order()];
        if (raw.Length == 0)
        {
            return;
        }

        // Sin transacción el FOR UPDATE se soltaría al terminar la sentencia y no frenaría a nadie.
        if (dbContext.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("LockRowsAsync requires the transaction of ICustomersUnitOfWork.BeginTransactionAsync.");
        }

        // ORDER BY id: dos borrados (o un borrado y un consentimiento) toman las filas en el mismo orden.
        await RunAsync(() => dbContext.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value" FROM customers.customers
            WHERE tenant_id = {tenantId} AND id = ANY({raw})
            ORDER BY id
            FOR UPDATE
            """).ToListAsync(cancellationToken));
    }

    public async Task<IReadOnlyDictionary<Guid, int>> SnapshotConsentsAsync(
        Guid tenantId,
        IReadOnlyCollection<(Guid CustomerId, string Name, string? PhoneE164)> customers,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(customers);
        var distinct = customers.DistinctBy(customer => customer.CustomerId).ToArray();
        if (distinct.Length == 0)
        {
            return new Dictionary<Guid, int>();
        }

        var ids = distinct.Select(customer => customer.CustomerId).ToArray();
        // left() por si acaso: el nombre del cliente ya tiene el mismo tope (Customer.NameMaxLength = 160), pero una fila vieja
        // más larga no debe tumbar el borrado entero.
        var names = distinct.Select(customer => customer.Name).ToArray();
        var phones = distinct.Select(customer => customer.PhoneE164).ToArray();

        // Un UPDATE por lote; el RETURNING dice cuántas filas de cada cliente tocó. Se materializa con ToListAsync: Postgres no
        // admite un UPDATE … RETURNING como subconsulta.
        var touched = await RunAsync(() => dbContext.Database.SqlQuery<Guid>($"""
            UPDATE customers.data_consents consent
               SET customer_name = left(snapshot.name, 160), customer_phone_e164 = snapshot.phone
              FROM unnest({ids}, {names}, {phones}) AS snapshot (customer_id, name, phone)
             WHERE consent.tenant_id = {tenantId} AND consent.customer_id = snapshot.customer_id
            RETURNING consent.customer_id AS "Value"
            """).ToListAsync(cancellationToken));
        return touched.GroupBy(id => id).ToDictionary(group => group.Key, group => group.Count());
    }

    public async Task<IReadOnlyDictionary<Guid, int>> DeleteAsync(
        Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        var customerIds = ToCustomerIds(ids);
        if (customerIds.Length == 0)
        {
            return new Dictionary<Guid, int>();
        }

        var raw = customerIds.Select(id => id.Value).ToArray();
        return await RunAsync(async () =>
        {
            // A-6: el merged= de la auditoría se cuenta antes; después del DELETE ya no hay fusionados que contar. SQL y no LINQ
            // por la misma razón que CustomerMergeStore.FindIdsMergedIntoAsync (merged_into_id es un CustomerId? con conversor).
            var merged = await dbContext.Database.SqlQuery<Guid>($"""
                SELECT merged_into_id AS "Value" FROM customers.customers
                WHERE tenant_id = {tenantId} AND merged_into_id = ANY({raw})
                """).ToListAsync(cancellationToken);

            // El cliente y sus fusionados en una sola sentencia: merged_into_id no tiene FK, así que el orden no importa. SQL y no
            // ExecuteDelete por el mismo conversor de merged_into_id.
            await dbContext.Database.ExecuteSqlAsync($"""
                DELETE FROM customers.customers
                WHERE tenant_id = {tenantId} AND (id = ANY({raw}) OR merged_into_id = ANY({raw}))
                """, cancellationToken);

            IReadOnlyDictionary<Guid, int> counts = merged.GroupBy(id => id).ToDictionary(group => group.Key, group => group.Count());
            return counts;
        });
    }

    private static Task<T> RunAsync<T>(Func<Task<T>> write) => CustomersWriteRaces.RunAsync(write);

    private static CustomerId[] ToCustomerIds(IReadOnlyCollection<Guid> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        return [.. ids.Distinct().Select(id => new CustomerId(id))];
    }
}
