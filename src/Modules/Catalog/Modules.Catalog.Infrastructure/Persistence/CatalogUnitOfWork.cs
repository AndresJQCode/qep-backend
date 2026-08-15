using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Modules.Catalog.Application;
using Modules.Catalog.Domain;
using Npgsql;

namespace Modules.Catalog.Infrastructure.Persistence;

internal sealed class CatalogUnitOfWork(CatalogDbContext dbContext) : ICatalogUnitOfWork
{
    // Discriminar por nombre de índice y no sólo por SqlState es deliberado: 23505 sólo dice
    // que se violó algún índice único, y responder catalog.product.code_taken para otro
    // mandaría al llamador a corregir el campo equivocado. Esa es la lección con la que se
    // cerró SDD-CT-06, donde otro índice único reportaba el código de dominio equivocado.
    private const string ProductCodeIndex = "IX_products_tenant_code";
    private const string TaxRateNameIndex = "IX_tax_rates_tenant_name";

    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await dbContext.SaveChangesAsync(cancellationToken);
        }
        // Va antes que la rama de DbUpdateException porque DbUpdateConcurrencyException hereda
        // de ella: al revés, el filtro de índice único la dejaría pasar sin traducir y saldría
        // como 500. Mismo patrón que TenancyUnitOfWork.
        catch (DbUpdateConcurrencyException exception)
        {
            // El mensaje es genérico a propósito: este catch cubre los dos agregados del módulo,
            // y decir "product" ante un conflicto de tasa mandaría a mirar la entidad equivocada.
            throw new RequestConcurrencyException(
                "concurrency.conflict",
                "The catalog record changed while the update was being committed.",
                exception);
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException postgres &&
                  postgres.SqlState == PostgresErrorCodes.UniqueViolation &&
                  string.Equals(
                      postgres.ConstraintName,
                      ProductCodeIndex,
                      StringComparison.Ordinal))
        {
            // Traducido acá y no en Application, que no referencia Npgsql y se mantiene así
            // gracias a CatalogLayerTests.
            throw new CatalogDomainException(
                "catalog.product.code_taken",
                "Another product in this tenant already uses that code.");
        }
        // Rama propia y no un `or` con la anterior: son dos índices únicos del mismo esquema y
        // cada uno tiene que devolver su código. Colapsarlos es exactamente el defecto de
        // SDD-CT-06 — mandar a corregir el campo equivocado.
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException postgres &&
                  postgres.SqlState == PostgresErrorCodes.UniqueViolation &&
                  string.Equals(
                      postgres.ConstraintName,
                      TaxRateNameIndex,
                      StringComparison.Ordinal))
        {
            throw new CatalogDomainException(
                "catalog.tax_rate.name_taken",
                "Another tax rate in this tenant already uses that name.");
        }
    }
}
