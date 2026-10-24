using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Modules.Suppliers.Application;
using Modules.Suppliers.Domain;
using Npgsql;

namespace Modules.Suppliers.Infrastructure.Persistence;

internal sealed class SuppliersUnitOfWork(SuppliersDbContext dbContext) : ISuppliersUnitOfWork
{
    // Se discrimina por nombre de índice y no sólo por SqlState: 23505 dice que se violó algún
    // índice único, no cuál (la lección de SDD-CT-06).
    private const string IdentificationIndex = "IX_suppliers_tenant_identification";

    // FK a geography.cities(id), nombrada a mano en la migración porque SuppliersDbContext no la
    // modela (ver el comentario en ConfigureSupplier).
    private const string CityForeignKey = "FK_suppliers_cities_city_id";

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        // Va antes que las ramas de DbUpdateException porque DbUpdateConcurrencyException hereda de
        // ella: al revés, saldría como 500 en vez de 412.
        catch (DbUpdateConcurrencyException exception)
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict",
                "The supplier changed while the update was being committed.",
                exception);
        }
        catch (DbUpdateException exception) when (IsUniqueViolationOf(exception, IdentificationIndex))
        {
            // Traducido acá y no en Application, que no referencia Npgsql (SuppliersLayerTests).
            throw new SuppliersDomainException(
                "suppliers.supplier.identification_taken",
                "Another supplier in this tenant already uses that identification.");
        }
        catch (DbUpdateException exception) when (IsForeignKeyViolationOf(exception, CityForeignKey))
        {
            // El handler ya comprueba la ciudad contra Geography; esto cubre la carrera con un
            // borrado entre la comprobación y el commit.
            throw new SuppliersDomainException(
                "suppliers.supplier.city_not_found",
                "The supplier city does not exist.");
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
}
