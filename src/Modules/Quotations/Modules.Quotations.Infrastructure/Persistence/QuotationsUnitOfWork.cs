using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Npgsql;

namespace Modules.Quotations.Infrastructure.Persistence;

internal sealed class QuotationsUnitOfWork(QuotationsDbContext dbContext) : IQuotationsUnitOfWork
{
    // Discriminar por nombre de indice y no solo por SqlState es deliberado -- la leccion de
    // SDD-CT-06: 23505 solo dice que se violo algun indice unico.
    private const string QuotationNumberIndex = "IX_quotations_tenant_number";

    // Order.QuotationId es 1:1 (IX_orders_quotation, único). En el camino normal no se alcanza:
    // convertir deja la cotización en Converted y EnsureConvertibleToOrder rechaza una segunda
    // conversión por estado. Queda de red para dos conversiones simultáneas que lean la cotización
    // antes de que cualquiera guarde, y para las convertidas antes de que existiera Converted, que
    // siguen en Sent (no hubo backfill). Sin traducir, saldría como 500 con el nombre de la
    // constraint adentro. Cambia junto con la migración que renombra el índice: la prueba es
    // OrderApiTests.ConvertingAQuotationThatAlreadyHasAnOrderIsAlreadyConverted.
    private const string OrderQuotationIndex = "IX_orders_quotation";

    // El número lo asigna IOrderNumberGenerator con un contador atómico por tenant, pero desde el
    // spec 2026-09-17 el formato es dato que se escribe a mano por SQL: un prefijo mal configurado
    // (por ejemplo uno que ya trae el año) puede armar el mismo texto que un pedido ya emitido y sí
    // alcanza este índice en la práctica. Sin traducir, saldría 500 con el nombre de la constraint
    // adentro. La prueba es
    // OrderApiTests.AMisconfiguredPrefixThatCollidesWithAnAlreadyIssuedOrderNumberIsRejected.
    private const string OrderNumberIndex = "IX_orders_tenant_number";

    // La PK del layout de columnas del Excel de pedidos (spec 2026-09-24, D9): sin fila, los dos
    // primeros PUT viajan con If-Match "1", pasan el chequeo de versión en memoria y los dos
    // intentan INSERT. El segundo choca acá, y es el mismo 412 que si la versión hubiera cambiado
    // —no un 422 de dominio, porque el tenant no hizo nada mal: alguien más guardó primero—. La
    // prueba es OrdersExportLayoutPersistenceTests.TwoFirstSavesForTheSameTenantEndInAConcurrencyConflict.
    private const string OrdersExportLayoutKey = "PK_orders_export_layouts";

    // The quotation settings keys (spec 2026-10-08, D7). QuotationSettingsStore.SaveAsync upserts and
    // locks the parent row inside the handler's transaction, so concurrent PUTs serialize and these
    // are not reached in the normal path. Safety net, like OrdersExportLayoutKey: a 412 and never a
    // 500 with the constraint name inside, should a future writer skip the store's lock.
    private const string QuotationSettingsKey = "PK_tenant_quotation_settings";

    private const string MinimumTotalsKey = "PK_tenant_minimum_totals";

    // La clave de idempotencia del pedido externo (spec de Shopify D-S23). ExternalOrderService busca antes de
    // crear, pero dos llamadas simultáneas con la misma clave (el webhook y la conciliación) pueden buscar las dos
    // antes de que alguna guarde: este índice cierra la carrera, y el servicio resuelve el código devolviendo el
    // pedido que ganó. La prueba es ExternalOrderServiceTests.CreateAsyncConcurrentSameExternalKeyCreatesOneOrder.
    private const string OrderExternalKeyIndex = "IX_orders_tenant_external_key";

    // El nombre de una transportadora, o cualquiera de sus alias, ya es de otra del mismo tenant
    // (spec 2026-10-17). Es la regla de negocio, no una red: la comprobación previa en el handler
    // no cierra la carrera entre dos altas simultáneas, este índice sí.
    private const string CarrierNameIndex = "IX_carrier_names_tenant_name";

    // Una parte por rol en cada pedido (spec 2026-10-26 §4.4). Dos trabajos de Shopify que ponen la parte de envío del mismo
    // pedido leen los dos el pedido sin ella y el segundo choca acá. Es el mismo 412 que si la versión del pedido hubiera
    // cambiado —ganó otro—: el trabajo reintenta y ve la parte ya puesta. La prueba es
    // ExternalOrderServiceTests.AttachShippingLosingTheRaceForTheShippingPartyIsAConcurrencyConflict.
    private const string OrderPartyRoleIndex = "IX_order_parties_order_role";

    // IOrderNumberGenerator recibe el mismo QuotationsDbContext scoped que esta clase, así que su
    // SQL crudo corre en esta conexión y queda dentro de la transacción. Si alguna vez se
    // registrara con un DbContext propio, el incremento volvería a autocommitearse aparte.
    public async Task<IQuotationsTransaction> BeginTransactionAsync(CancellationToken cancellationToken) =>
        new QuotationsTransaction(await dbContext.Database.BeginTransactionAsync(cancellationToken));

    private sealed class QuotationsTransaction(IDbContextTransaction transaction) : IQuotationsTransaction
    {
        public Task CommitAsync(CancellationToken cancellationToken) =>
            transaction.CommitAsync(cancellationToken);

        // Disponer sin commit hace rollback, y el rollback es lo que suelta el lock del contador.
        public ValueTask DisposeAsync() => transaction.DisposeAsync();
    }

    public void DiscardPendingChanges() => dbContext.ChangeTracker.Clear();

    // Borrar clientes (spec 2026-11-10, P1/P2): guardar una cotización o un pedido con cliente es escribir una
    // referencia a customers.customers, que no tiene FK. Para que el borrado en bloque no deje una referencia
    // huérfana, el escritor toma el candado compartido del cliente y, ya con él, comprueba que la fila exista, todo
    // dentro de la transacción que guarda. Si el borrado ganó la carrera, el candado espera a su commit y la
    // comprobación ve el cliente ya borrado. Pasan por aquí todos los caminos de escritura, incluido el
    // IExternalOrderWriter (Shopify y tienda pública), porque comparten esta unidad de trabajo.
    private async Task<int> SaveHoldingCustomerReferencesAsync(CancellationToken cancellationToken)
    {
        var references = PendingCustomerReferences();
        if (references.Count == 0)
        {
            return await dbContext.SaveChangesAsync(cancellationToken);
        }

        // Con transacción abierta (el handler ya la trajo por el contador de numeración) el candado queda en ella.
        // Sin ella, el candado de transacción se soltaría al terminar su propia sentencia: se abre una aquí.
        if (dbContext.Database.CurrentTransaction is not null)
        {
            await HoldCustomerReferencesAsync(references, cancellationToken);
            return await dbContext.SaveChangesAsync(cancellationToken);
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await HoldCustomerReferencesAsync(references, cancellationToken);
        var saved = await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return saved;
    }

    // Clientes distintos y no vacíos de toda cotización o pedido nuevo, o con ClientId modificado, ordenados para
    // tomar los candados siempre en el mismo orden. Un pedido manual sin cliente (Guid.Empty) no referencia a nadie.
    private List<CustomerReference> PendingCustomerReferences()
    {
        var found = new Dictionary<(Guid TenantId, Guid ClientId), string>();

        foreach (var entry in dbContext.ChangeTracker.Entries())
        {
            if (entry.Entity is not (Quotation or Order)
                || entry.State is not (EntityState.Added or EntityState.Modified))
            {
                continue;
            }

            var clientId = entry.Property(nameof(Quotation.ClientId));
            if (entry.State == EntityState.Modified && !clientId.IsModified)
            {
                continue;
            }

            var id = (Guid)clientId.CurrentValue!;
            if (id == Guid.Empty)
            {
                continue;
            }

            var tenantId = (Guid)entry.Property(nameof(Quotation.TenantId)).CurrentValue!;
            var code = entry.Entity is Quotation
                ? "quotation.quotation.client_not_found"
                : "order.order.client_not_found";
            // Si el mismo cliente aparece en una cotización y en un pedido, gana el código de la cotización.
            if (!found.TryGetValue((tenantId, id), out var existing) || existing.StartsWith("order.", StringComparison.Ordinal))
            {
                found[(tenantId, id)] = code;
            }
        }

        return found
            .OrderBy(pair => pair.Key.TenantId)
            .ThenBy(pair => pair.Key.ClientId)
            .Select(pair => new CustomerReference(pair.Key.TenantId, pair.Key.ClientId, pair.Value))
            .ToList();
    }

    private async Task HoldCustomerReferencesAsync(
        List<CustomerReference> references, CancellationToken cancellationToken)
    {
        foreach (var reference in references)
        {
            // Primero el candado y después la lectura (P1): al revés, se vería un cliente a punto de desaparecer.
            await dbContext.Database.ExecuteSqlAsync(
                CustomerReferenceLock.Shared(reference.TenantId, reference.CustomerId), cancellationToken);

            var exists = await dbContext.Database
                .SqlQuery<bool>(CustomerReferenceLock.Exists(reference.TenantId, reference.CustomerId))
                .SingleAsync(cancellationToken);
            if (!exists)
            {
                throw new QuotationsDomainException(
                    reference.NotFoundCode,
                    $"Client '{reference.CustomerId}' was not found in this tenant.");
            }
        }
    }

    private sealed record CustomerReference(Guid TenantId, Guid CustomerId, string NotFoundCode);

    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await SaveHoldingCustomerReferencesAsync(cancellationToken);
        }
        // Antes que DbUpdateException porque DbUpdateConcurrencyException hereda de ella: al
        // reves, el filtro de indice unico la dejaria pasar sin traducir y saldria como 500.
        // Mismo patron que CatalogUnitOfWork/CustomersUnitOfWork.
        catch (DbUpdateConcurrencyException exception)
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict",
                "The quotation changed while the update was being committed.",
                exception);
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException postgres &&
                  postgres.SqlState == PostgresErrorCodes.UniqueViolation &&
                  string.Equals(
                      postgres.ConstraintName,
                      QuotationNumberIndex,
                      StringComparison.Ordinal))
        {
            // No deberia alcanzarse en la practica -- el numero lo asigna
            // IQuotationNumberGenerator con un contador atomico por tenant -- pero traducido
            // igual: un 500 con el nombre de la constraint adentro no le dice nada al llamador.
            throw new QuotationsDomainException(
                "quotation.quotation.number_taken",
                "Another quotation in this tenant already uses that number.");
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException postgres &&
                  postgres.SqlState == PostgresErrorCodes.UniqueViolation &&
                  string.Equals(
                      postgres.ConstraintName,
                      OrderQuotationIndex,
                      StringComparison.Ordinal))
        {
            throw new QuotationsDomainException(
                "quotation.quotation.already_converted",
                "This quotation was already converted to an order.");
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException postgres &&
                  postgres.SqlState == PostgresErrorCodes.UniqueViolation &&
                  string.Equals(
                      postgres.ConstraintName,
                      OrderNumberIndex,
                      StringComparison.Ordinal))
        {
            throw new QuotationsDomainException(
                "order.order.number_taken",
                "Another order in this tenant already uses that number.");
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException postgres &&
                  postgres.SqlState == PostgresErrorCodes.UniqueViolation &&
                  string.Equals(
                      postgres.ConstraintName,
                      OrderExternalKeyIndex,
                      StringComparison.Ordinal))
        {
            throw new QuotationsDomainException(
                ExternalOrderErrors.ExternalKeyTaken,
                "Another order from this source already uses that external key.");
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException postgres &&
                  postgres.SqlState == PostgresErrorCodes.UniqueViolation &&
                  string.Equals(
                      postgres.ConstraintName,
                      CarrierNameIndex,
                      StringComparison.Ordinal))
        {
            throw new QuotationsDomainException(
                "quotations.carrier.name_taken",
                "Ya hay una transportadora con ese nombre o con alguno de esos nombres externos.");
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException postgres &&
                  postgres.SqlState == PostgresErrorCodes.UniqueViolation &&
                  string.Equals(
                      postgres.ConstraintName,
                      OrdersExportLayoutKey,
                      StringComparison.Ordinal))
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict",
                "The orders export layout was created by another request while this one was being committed.",
                exception);
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException postgres &&
                  postgres.SqlState == PostgresErrorCodes.UniqueViolation &&
                  (string.Equals(postgres.ConstraintName, QuotationSettingsKey, StringComparison.Ordinal) ||
                   string.Equals(postgres.ConstraintName, MinimumTotalsKey, StringComparison.Ordinal)))
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict",
                "The quotation settings were saved by another request while this one was being committed.",
                exception);
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException postgres &&
                  postgres.SqlState == PostgresErrorCodes.UniqueViolation &&
                  string.Equals(postgres.ConstraintName, OrderPartyRoleIndex, StringComparison.Ordinal))
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict",
                "The order party was written by another process while this one was being committed.",
                exception);
        }
    }
}
