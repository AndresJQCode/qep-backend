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

    public async Task<int> CountContactsMovedAsync(
        Guid tenantId, Guid survivorId, IReadOnlyCollection<Guid> mergedIds, CancellationToken cancellationToken)
    {
        var ids = ToCustomerIds(mergedIds);
        if (ids.Length == 0)
        {
            return 0;
        }

        return await MovingContacts(tenantId, new CustomerId(survivorId), ids).CountAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> RepointContactsAsync(
        Guid tenantId, Guid survivorId, IReadOnlyCollection<Guid> mergedIds, CancellationToken cancellationToken)
    {
        var ids = ToCustomerIds(mergedIds);
        if (ids.Length == 0)
        {
            return [];
        }

        CustomerId? survivor = new CustomerId(survivorId);
        var survivorKey = new CustomerId(survivorId);
        // Leer y después mover no deja hueco: la fusión ya tiene las filas de los fusionados con FOR UPDATE, y vincular un contacto
        // a una empresa bloquea la fila de la empresa, así que nadie entra ni sale de este conjunto entre las dos sentencias.
        var moving = await MovingContacts(tenantId, survivorKey, ids).Select(customer => customer.Id).ToListAsync(cancellationToken);
        if (moving.Count == 0)
        {
            return [];
        }

        var movingIds = moving.ToArray();
        await CustomersWriteRaces.RunAsync(() => MovingContacts(tenantId, survivorKey, ids)
            .Where(customer => movingIds.Contains(customer.Id))
            .ExecuteUpdateAsync(setters => setters.SetProperty(customer => customer.CompanyId, survivor), cancellationToken));
        return moving.Select(id => id.Value).ToArray();
    }

    // Sin fusionados: un contacto que también se fusiona queda como tombstone sin empresa, no se mueve. Sin el que queda: no
    // puede quedar apuntándose a sí mismo (su vínculo, si apuntaba a un fusionado, lo borra FillSurvivor).
    private IQueryable<Customer> MovingContacts(Guid tenantId, CustomerId survivor, CustomerId[] mergedIds) =>
        dbContext.Customers.Where(customer =>
            customer.TenantId == tenantId
            && customer.CompanyId != null && mergedIds.Contains(customer.CompanyId.Value)
            && customer.MergedIntoId == null
            && customer.Id != survivor
            && !mergedIds.Contains(customer.Id));

    public async Task<CustomerMergeOwnedCounts> CountOwnedAsync(
        Guid tenantId, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken)
    {
        var ids = ToCustomerIds(customerIds);
        if (ids.Length == 0)
        {
            return new CustomerMergeOwnedCounts(0, 0, 0);
        }

        var notes = await dbContext.CustomerNotes
            .CountAsync(note => note.TenantId == tenantId && ids.Contains(note.CustomerId), cancellationToken);
        var addresses = await OwnedAddresses(tenantId, ids).CountAsync(cancellationToken);
        var billingProfiles = await OwnedBillingProfiles(tenantId, ids).CountAsync(cancellationToken);
        return new CustomerMergeOwnedCounts(notes, addresses, billingProfiles);
    }

    public Task<Customer> ReadSurvivorAsync(Guid tenantId, Guid survivorId, CancellationToken cancellationToken)
    {
        var id = new CustomerId(survivorId);
        return dbContext.Customers
            .AsNoTracking()
            .Include(customer => customer.Addresses)
            .Include(customer => customer.BillingProfiles)
            .AsSingleQuery()
            .SingleAsync(customer => customer.TenantId == tenantId && customer.Id == id, cancellationToken);
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
        await MoveBillingProfilesAsync(tenantId, survivor, ids, cancellationToken);
        // Spec 2026-10-26 D-7: el historial de consentimiento es del cliente y pasa entero al que queda. Sólo cambia customer_id
        // (es prueba legal: nada se recalcula ni se renumera, y el vigente sigue saliendo de (occurred_at, sequence)). El
        // tenant filtra: las filas de otro tenant no se tocan aunque su id venga en la lista.
        _ = await dbContext.DataConsents
            .Where(consent => consent.TenantId == tenantId && ids.Contains(consent.CustomerId))
            .ExecuteUpdateAsync(setters => setters.SetProperty(consent => consent.CustomerId, survivor), cancellationToken);
    }

    // Owner, 2026-11-25: se mueven todos, sin deduplicar y sin tope, como no principales. A diferencia de la libreta de
    // direcciones, el que queda puede no tener ningún perfil: entonces el movido más antiguo (created_at, id) queda
    // principal, porque una libreta con perfiles siempre tiene uno.
    private async Task MoveBillingProfilesAsync(
        Guid tenantId, CustomerId survivor, CustomerId[] ids, CancellationToken cancellationToken)
    {
        // Antes de mover: después ya no se distingue lo propio de lo recibido.
        var survivorHadProfiles = await OwnedBillingProfiles(tenantId, [survivor]).AnyAsync(cancellationToken);
        var oldest = await OwnedBillingProfiles(tenantId, ids)
            .OrderBy(profile => profile.CreatedAt)
            .ThenBy(profile => profile.Id)
            .Select(profile => profile.Id)
            .Take(1)
            .ToListAsync(cancellationToken);
        if (oldest.Count == 0)
        {
            return;
        }

        _ = await OwnedBillingProfiles(tenantId, ids)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(profile => profile.CustomerId, survivor)
                    .SetProperty(profile => profile.IsPrincipal, false),
                cancellationToken);
        if (survivorHadProfiles)
        {
            return;
        }

        var principal = oldest[0];
        _ = await dbContext.CustomerBillingProfiles
            .Where(profile => profile.Id == principal)
            .ExecuteUpdateAsync(setters => setters.SetProperty(profile => profile.IsPrincipal, true), cancellationToken);
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

    // Igual que OwnedAddresses: la tabla no tiene tenant_id.
    private IQueryable<CustomerBillingProfile> OwnedBillingProfiles(Guid tenantId, CustomerId[] ids) =>
        dbContext.CustomerBillingProfiles.Where(profile =>
            ids.Contains(profile.CustomerId) &&
            dbContext.Customers.Any(customer => customer.TenantId == tenantId && customer.Id == profile.CustomerId));

    private static Guid[] Distinct(IReadOnlyCollection<Guid> customerIds)
    {
        ArgumentNullException.ThrowIfNull(customerIds);
        return customerIds.Distinct().ToArray();
    }

    private static CustomerId[] ToCustomerIds(IReadOnlyCollection<Guid> customerIds) =>
        Distinct(customerIds).Select(id => new CustomerId(id)).ToArray();
}
