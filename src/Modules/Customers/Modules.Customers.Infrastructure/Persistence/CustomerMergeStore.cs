using Microsoft.EntityFrameworkCore;
using Modules.Customers.Application;
using Modules.Customers.Domain;

namespace Modules.Customers.Infrastructure.Persistence;

/// <summary>
/// Spec 2026-10-24 §3.2–§3.4. Sentencias directas (<c>ExecuteUpdate</c> y consultas sin tracking) sobre el
/// <see cref="CustomersDbContext"/> del scope: no cargan entidades y, con la transacción de
/// <see cref="CustomersUnitOfWork.BeginTransactionAsync"/> abierta, usan su conexión y quedan dentro de ella.
///
/// Ninguna de estas sentencias puede violar un índice único (mueven filas por <c>customer_id</c>, que no es único, o
/// reescriben <c>merged_into_id</c>, que tampoco): las violaciones de la fusión salen de los <c>SaveChanges</c> del agregado
/// y las traduce <see cref="CustomersUnitOfWork"/> por nombre de índice. Lo único que se traduce acá es la carrera (deadlock o
/// serialización, <see cref="CustomersWriteRaces"/>), porque estas sentencias no pasan por el unit of work.
/// </summary>
internal sealed class CustomerMergeStore(CustomersDbContext dbContext) : ICustomerMergeStore
{
    public async Task<IReadOnlyList<Guid>> FindIdsMergedIntoAsync(
        Guid tenantId, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken)
    {
        var ids = Distinct(customerIds);
        if (ids.Length == 0)
        {
            return [];
        }

        // SQL y no LINQ: merged_into_id es un CustomerId? con conversor, y un Contains sobre él no es una traducción en la
        // que convenga confiar. Lo sirve el índice parcial IX_customers_tenant_merged_into.
        return await dbContext.Database
            .SqlQuery<Guid>($"""
                SELECT id AS "Value" FROM customers.customers
                WHERE tenant_id = {tenantId} AND merged_into_id = ANY({ids})
                ORDER BY id
                """)
            .ToListAsync(cancellationToken);
    }

    public async Task RepointMergedIntoAsync(
        Guid tenantId, IReadOnlyCollection<Guid> customerIds, Guid survivorId, CancellationToken cancellationToken)
    {
        var ids = ToCustomerIds(customerIds);
        if (ids.Length == 0)
        {
            return;
        }

        CustomerId? survivor = new CustomerId(survivorId);
        await CustomersWriteRaces.RunAsync(() => dbContext.Customers
            .Where(customer => customer.TenantId == tenantId && ids.Contains(customer.Id) && customer.MergedIntoId != null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(customer => customer.MergedIntoId, survivor), cancellationToken));
    }

    public async Task<CustomerMergeOwnedCounts> CountOwnedAsync(
        Guid tenantId, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken)
    {
        var ids = ToCustomerIds(customerIds);
        if (ids.Length == 0)
        {
            return new CustomerMergeOwnedCounts(0, 0);
        }

        var notes = await dbContext.CustomerNotes
            .CountAsync(note => note.TenantId == tenantId && ids.Contains(note.CustomerId), cancellationToken);
        var addresses = await OwnedAddresses(tenantId, ids).CountAsync(cancellationToken);
        return new CustomerMergeOwnedCounts(notes, addresses);
    }

    public async Task MoveOwnedAsync(
        Guid tenantId, Guid survivorId, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken)
    {
        var ids = ToCustomerIds(customerIds);
        if (ids.Length == 0)
        {
            return;
        }

        var survivor = new CustomerId(survivorId);
        await CustomersWriteRaces.RunAsync(() => MoveOwnedCoreAsync(tenantId, survivor, ids, cancellationToken));
    }

    private async Task MoveOwnedCoreAsync(Guid tenantId, CustomerId survivor, CustomerId[] ids, CancellationToken cancellationToken)
    {
        // La nota es su propio agregado y su versión es la del texto: moverla de cliente no la edita, así que no la sube
        // (una pestaña con la nota abierta puede seguir editándola).
        _ = await dbContext.CustomerNotes
            .Where(note => note.TenantId == tenantId && ids.Contains(note.CustomerId))
            .ExecuteUpdateAsync(setters => setters.SetProperty(note => note.CustomerId, survivor), cancellationToken);
        // Las del fusionado llegan como no principales: la principal del que queda sigue siendo la única (§3.2 paso 3, el
        // invariante de Customer.ApplyPrincipal).
        _ = await OwnedAddresses(tenantId, ids)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(address => address.CustomerId, survivor)
                    .SetProperty(address => address.IsPrincipal, false),
                cancellationToken);
        // Spec 2026-10-26 D-7: el historial de consentimiento es del cliente y pasa entero al que queda. Sólo cambia customer_id
        // (es prueba legal: nada se recalcula ni se renumera, y el vigente sigue saliendo de (occurred_at, sequence)). El
        // tenant filtra: las filas de otro tenant no se tocan aunque su id venga en la lista.
        _ = await dbContext.DataConsents
            .Where(consent => consent.TenantId == tenantId && ids.Contains(consent.CustomerId))
            .ExecuteUpdateAsync(setters => setters.SetProperty(consent => consent.CustomerId, survivor), cancellationToken);
    }

    public async Task<Guid?> FindSurvivorIdAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken)
    {
        var id = new CustomerId(customerId);
        var survivor = await dbContext.Customers
            .AsNoTracking()
            .Where(customer => customer.TenantId == tenantId && customer.Id == id)
            .Select(customer => customer.MergedIntoId)
            .FirstOrDefaultAsync(cancellationToken);
        return survivor?.Value;
    }

    public async Task<Guid?> FindSurvivorIdByIdentificationAsync(
        Guid tenantId, IdentificationType type, string number, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(number);
        var survivor = await dbContext.Customers
            .AsNoTracking()
            .Where(customer =>
                customer.TenantId == tenantId &&
                customer.MergedIntoId != null &&
                customer.IdentificationType == type &&
                customer.IdentificationNumber == number)
            .OrderBy(customer => customer.CreatedAt)
            .ThenBy(customer => customer.Id)
            .Select(customer => customer.MergedIntoId)
            .FirstOrDefaultAsync(cancellationToken);
        return survivor?.Value;
    }

    // customer_addresses no tiene tenant_id: el tenant lo da su cliente. Sin este filtro, un id de otro tenant movería sus
    // direcciones.
    private IQueryable<CustomerAddress> OwnedAddresses(Guid tenantId, CustomerId[] ids) =>
        dbContext.CustomerAddresses.Where(address =>
            ids.Contains(address.CustomerId) &&
            dbContext.Customers.Any(customer => customer.TenantId == tenantId && customer.Id == address.CustomerId));

    private static Guid[] Distinct(IReadOnlyCollection<Guid> customerIds)
    {
        ArgumentNullException.ThrowIfNull(customerIds);
        return customerIds.Distinct().ToArray();
    }

    private static CustomerId[] ToCustomerIds(IReadOnlyCollection<Guid> customerIds) =>
        Distinct(customerIds).Select(id => new CustomerId(id)).ToArray();
}
