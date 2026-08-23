using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
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
            when (IsForeignKeyViolationOf(exception, ClassificationForeignKey) &&
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

    private static bool IsUniqueViolationOf(DbUpdateException exception, string indexName) =>
        exception.InnerException is PostgresException postgres &&
        postgres.SqlState == PostgresErrorCodes.UniqueViolation &&
        string.Equals(postgres.ConstraintName, indexName, StringComparison.Ordinal);

    private static bool IsForeignKeyViolationOf(DbUpdateException exception, string constraintName) =>
        exception.InnerException is PostgresException postgres &&
        postgres.SqlState == PostgresErrorCodes.ForeignKeyViolation &&
        string.Equals(postgres.ConstraintName, constraintName, StringComparison.Ordinal);

    // El estado sigue siendo Deleted cuando SaveChanges falla: EF solo lo pasa a Detached despues
    // de un commit exitoso.
    private static bool IsDeletingAClassification(DbUpdateException exception) =>
        exception.Entries.Any(entry =>
            entry.Entity is ClientClassification && entry.State == EntityState.Deleted);
}
