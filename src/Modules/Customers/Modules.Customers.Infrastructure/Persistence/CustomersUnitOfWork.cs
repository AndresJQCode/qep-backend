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
    }

    private static bool IsUniqueViolationOf(DbUpdateException exception, string indexName) =>
        exception.InnerException is PostgresException postgres &&
        postgres.SqlState == PostgresErrorCodes.UniqueViolation &&
        string.Equals(postgres.ConstraintName, indexName, StringComparison.Ordinal);
}
