using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Modules.Customers.Application;
using Modules.Customers.Domain;
using Npgsql;

namespace Modules.Customers.Infrastructure.Persistence;

internal sealed class CustomersUnitOfWork(CustomersDbContext dbContext) : ICustomersUnitOfWork
{
    // Discriminar por nombre de indice y no solo por SqlState es deliberado: 23505 solo dice que
    // se violo algun indice unico, y responder el codigo del otro campo mandaria al llamador a
    // corregir el equivocado. Esa es la leccion con la que se cerro SDD-CT-06 — y aca importa mas
    // que en empresas, porque este esquema tiene **dos** indices unicos y hay que distinguirlos.
    private const string IdentificationIndex = "IX_customers_tenant_identification";

    private const string CucIndex = "IX_customers_tenant_cuc";

    // Interna: CustomerRepository la usa para traducir sus UPDATE condicionales, que no pasan por SaveChanges.
    internal const string WhatsAppUserIdIndex = "IX_customers_tenant_whatsapp_user_id";

    /// <summary>Spec 2026-10-14 decisión 10.</summary>
    internal const string InstagramUserIdIndex = "IX_customers_tenant_instagram_user_id";

    private const string TagForeignKey = "FK_customer_tags_tags_tag_id";

    private const string ClassificationNameIndex = "IX_client_classifications_tenant_name";

    private const string ClassificationPrefixIndex = "IX_client_classifications_tenant_prefix";

    // FK a customers.client_classifications(tenant_id, id) — nombrada a mano en
    // CustomersDbContext con HasConstraintName, asi que su nombre no depende de la convencion de
    // EF Core (que cambiaria si alguien reordena las propiedades de la FK compuesta).
    private const string ClassificationForeignKey =
        "FK_customers_client_classifications_classification_id";

    // FK a geography.cities(id) — nombrada a mano en la migracion que la agrega, porque no la
    // modela CustomersDbContext (ver el comentario en ConfigureCustomer).
    private const string CityForeignKey = "FK_customers_cities_city_id";

    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await dbContext.SaveChangesAsync(cancellationToken);
        }
        // Un deadlock o una falla de serialización (CustomersWriteRaces): 412, no 500 ni 409. La estrategia de Npgsql lo
        // envuelve en un InvalidOperationException, así que no lo atrapa ninguna rama de DbUpdateException de abajo.
        catch (Exception exception) when (CustomersWriteRaces.IsRace(exception))
        {
            throw CustomersWriteRaces.ToConflict(exception);
        }
        // Va antes que las ramas de DbUpdateException porque DbUpdateConcurrencyException hereda de
        // ella: al reves, el filtro de indice unico la dejaria pasar sin traducir y saldria como
        // 500. Mismo patron que TenancyUnitOfWork, CatalogUnitOfWork y CompaniesUnitOfWork.
        catch (DbUpdateConcurrencyException exception)
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict",
                "The customer changed while the update was being committed.",
                exception);
        }
        catch (DbUpdateException exception) when (IsUniqueViolationOf(exception, IdentificationIndex))
        {
            // Traducido aca y no en Application, que no referencia Npgsql y se mantiene asi gracias
            // a CustomersLayerTests.
            //
            // El tracker se limpia, como con el BSUID: CustomerProvisioning (spec de Shopify §6.2.1) pierde esta carrera,
            // relee al ganador y sigue en el mismo scope; sin el Clear, la fila perdedora y su outbox quedarían Added y
            // el siguiente SaveChanges los reintentaría. En el POST y en la importación el scope termina con este 422, así
            // que ahí no cambia nada. Ojo: Clear descarta cualquier otro cambio pendiente del scope, no sólo esa fila.
            dbContext.ChangeTracker.Clear();
            throw new CustomersDomainException(
                "customers.customer.identification_taken",
                "Another customer in this tenant already uses that identification.");
        }
        catch (DbUpdateException exception) when (IsUniqueViolationOf(exception, CucIndex))
        {
            // Su propia rama y su propio codigo, nunca un `or` con la anterior. Un CUC repetido no
            // es culpa de nadie que este llenando el formulario —el codigo lo emite el backend—,
            // asi que mandarlo a corregir el documento seria mandarlo a corregir un campo que esta
            // bien. Llegar aca significa que el consecutivo se desincronizo, y eso es un problema
            // del servidor.
            throw new CustomersDomainException(
                "customers.customer.cuc_taken",
                "The generated CUC is already in use for this tenant.");
        }
        catch (DbUpdateException exception) when (IsUniqueViolationOf(exception, WhatsAppUserIdIndex))
        {
            // Spec 2026-10-10 §9.4 (P3): dos entregas del mismo BSUID nuevo. CustomerWhatsAppDirectory relee. El tracker
            // se limpia: la fila que no entró quedaría Added y el siguiente SaveChanges del scope (la ingesta procesa
            // varios mensajes por entrega) la volvería a intentar. Ojo: Clear descarta también cualquier otro cambio
            // pendiente del scope, no sólo esa fila. Hoy el único que escribe un BSUID nuevo con SaveChanges es
            // CustomerWhatsAppDirectory, en un scope sin más cambios; quien lo reutilice tiene que saberlo.
            dbContext.ChangeTracker.Clear();
            throw new WhatsAppUserIdTakenException(exception);
        }
        catch (DbUpdateException exception) when (IsUniqueViolationOf(exception, InstagramUserIdIndex))
        {
            // Spec 2026-10-14 decisión 10: dos entregas del mismo IGSID nuevo. Mismo tratamiento que el BSUID: CustomerInstagramDirectory relee.
            dbContext.ChangeTracker.Clear();
            throw new InstagramUserIdTakenException(exception);
        }
        catch (DbUpdateException exception)
            when (IsUniqueViolationOf(exception, CustomersDbContext.TagNameIndex))
        {
            // Spec 2026-10-11 §4.2: 409 y no 422. El nombre repetido es un choque con lo que ya existe, que el
            // cliente resuelve eligiendo otro nombre; por eso es ResourceConflictException y no un error de
            // dominio. Por nombre de índice, como los demás, aunque hoy sea el único único de tags.
            throw new ResourceConflictException(
                "customers.tag.name_taken", "Another tag in this tenant already uses that name.", exception);
        }
        catch (DbUpdateException exception)
            when (IsUniqueViolationOf(exception, CustomersDbContext.CustomerTagPrimaryKey))
        {
            // Dos PUT de etiquetas del mismo cliente que agregan la misma etiqueta a la vez: el segundo perdió.
            throw new RequestConcurrencyException(
                "concurrency.conflict", "The customer's tags changed while they were being saved.", exception);
        }
        catch (DbUpdateException exception)
            when (IsForeignKeyViolationOf(exception, TagForeignKey))
        {
            // La etiqueta se borró entre la lectura y el commit.
            throw new ResourceNotFoundException("customers.tag.not_found", "A tag was deleted while it was being assigned.");
        }
        catch (DbUpdateException exception)
            when (IsUniqueViolationOf(exception, ClassificationNameIndex))
        {
            throw new CustomersDomainException(
                "customers.classification.name_taken",
                "Another client classification in this tenant already uses that name.");
        }
        catch (DbUpdateException exception)
            when (IsUniqueViolationOf(exception, ClassificationPrefixIndex))
        {
            throw new CustomersDomainException(
                "customers.classification.prefix_taken",
                "Another client classification in this tenant already uses that prefix.");
        }
        // La MISMA constraint se viola por dos causas opuestas, mismo caso que
        // FK_products_tax_rates_tax_rate_id en Catalog (CAT-06):
        //
        //   - Guardando un Customer que apunta a una clasificacion inexistente/de otro tenant ->
        //     la clasificacion no existe.
        //   - Borrando un ClientClassification que algun cliente sigue usando -> esta en uso.
        //
        // El SqlState y el nombre de la constraint son identicos en los dos, asi que se distingue
        // por que entidad estaba guardando EF (Entries), no por el error de Postgres. Por HTTP
        // esta rama especifica no se alcanza normalmente: DeleteClientClassificationHandler ya
        // frena antes con AnyWithClassificationAsync. Lo que queda vivo es la carrera —que un
        // cliente que la use se cree entre esa consulta y el commit— y esta red la traduce igual
        // en vez de dejarla salir como 500.
        //
        // Esta rama va primero porque es la mas especifica de las dos.
        catch (DbUpdateException exception)
            when (IsForeignKeyOrRestrictViolationOf(exception, ClassificationForeignKey) &&
                  IsDeletingAClassification(exception))
        {
            throw new CustomersDomainException(
                "customers.classification.in_use",
                "The client classification cannot be deleted because at least one customer " +
                "uses it.");
        }
        catch (DbUpdateException exception)
            when (IsForeignKeyViolationOf(exception, ClassificationForeignKey))
        {
            throw new CustomersDomainException(
                "customers.customer.classification_not_found",
                "The client classification was not found in this tenant.");
        }
        catch (DbUpdateException exception) when (IsForeignKeyViolationOf(exception, CityForeignKey))
        {
            throw new CustomersDomainException(
                "customers.customer.city_not_found",
                "The city was not found.");
        }
    }

    // Copia de CatalogUnitOfWork.BeginTransactionAsync. Los SaveChanges que corran dentro siguen pasando por la traducción de
    // arriba: una violación de índice único de la fusión sale con su código y el Dispose sin Commit deshace todo.
    public async Task<ICustomersTransaction> BeginTransactionAsync(CancellationToken cancellationToken) =>
        new CustomersTransaction(await dbContext.Database.BeginTransactionAsync(cancellationToken));

    private sealed class CustomersTransaction(IDbContextTransaction transaction) : ICustomersTransaction
    {
        public Task CommitAsync(CancellationToken cancellationToken) =>
            CustomersWriteRaces.RunAsync(() => transaction.CommitAsync(cancellationToken));

        public ValueTask DisposeAsync() => transaction.DisposeAsync();
    }

    private static bool IsUniqueViolationOf(DbUpdateException exception, string indexName) =>
        exception.InnerException is PostgresException postgres &&
        postgres.SqlState == PostgresErrorCodes.UniqueViolation &&
        string.Equals(postgres.ConstraintName, indexName, StringComparison.Ordinal);

    private static bool IsForeignKeyViolationOf(DbUpdateException exception, string constraintName) =>
        exception.InnerException is PostgresException postgres &&
        postgres.SqlState == PostgresErrorCodes.ForeignKeyViolation &&
        string.Equals(postgres.ConstraintName, constraintName, StringComparison.Ordinal);

    // Para el lado del borrado: la FK de la clasificación es ON DELETE RESTRICT, y borrar la fila padre que alguien usa da
    // 23001 (restrict_violation), no 23503. Insertar o actualizar la hija sigue dando 23503, por eso las demás ramas usan
    // IsForeignKeyViolationOf. El nombre de la restricción sigue siendo lo que decide.
    private static bool IsForeignKeyOrRestrictViolationOf(DbUpdateException exception, string constraintName) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.ForeignKeyViolation or PostgresErrorCodes.RestrictViolation,
        } postgres &&
        string.Equals(postgres.ConstraintName, constraintName, StringComparison.Ordinal);

    // El estado sigue siendo Deleted cuando SaveChanges falla: EF solo lo pasa a Detached despues
    // de un commit exitoso.
    private static bool IsDeletingAClassification(DbUpdateException exception) =>
        exception.Entries.Any(entry =>
            entry.Entity is ClientClassification && entry.State == EntityState.Deleted);
}
