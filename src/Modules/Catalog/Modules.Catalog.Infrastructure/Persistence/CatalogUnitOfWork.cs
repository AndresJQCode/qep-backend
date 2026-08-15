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
    private const string ProductTaxRateForeignKey = "FK_products_tax_rates_tax_rate_id";

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
        // Hallazgo `B` de la revisión de 4 lentes de CAT-04. La FK que estrena AddProductDetails
        // va con RESTRICT, y su violación no estaba traducida: salía como 500 server.unexpected
        // y —por el hallazgo `C`, que es de ApiExceptionHandler y no de este módulo— con el
        // nombre de la constraint adentro del mensaje.
        //
        // Por HTTP este camino no se alcanza: ProductTaxRateResolver frena antes al taxRateId
        // que no existe o es de otro tenant, y CA-CAT-04-08 lo cubre. Lo que queda vivo es la
        // carrera —que la fila desaparezca entre la verificación y el commit— y el borrado por
        // SQL, que es exactamente el escenario para el que se puso el RESTRICT. La red de abajo
        // se traduce igual: un 500 en ese caso no le dice nada a nadie.
        //
        // Mismo código de dominio que el resolver a propósito: para el llamador es el mismo
        // problema —la tasa que pidió no está— y darle dos códigos distintos según qué capa lo
        // detectó lo obligaría a manejar los dos.
        // CAT-06: la MISMA constraint se viola por dos causas opuestas, y cada una manda a
        // corregir una entidad distinta.
        //
        //   - Escribiendo un Product que apunta a una tasa inexistente -> la tasa no existe.
        //   - Borrando un TaxRate que algún producto sigue usando      -> la tasa está en uso.
        //
        // El SqlState y el nombre de la constraint son idénticos en los dos, así que no alcanza
        // con mirarlos: se distingue por **qué entidad estaba guardando EF**, que es lo que
        // Entries expone. Colapsarlos en un solo código manda a mirar la entidad equivocada —
        // la misma lección de SDD-CT-06, un nivel más adentro.
        //
        // Esta rama va primero porque es la más específica de las dos.
        catch (DbUpdateException exception)
            when (IsProductTaxRateViolation(exception) && IsDeletingATaxRate(exception))
        {
            throw new CatalogDomainException(
                "catalog.tax_rate.in_use",
                "The tax rate cannot be deleted because at least one product uses it.");
        }
        catch (DbUpdateException exception) when (IsProductTaxRateViolation(exception))
        {
            throw new CatalogDomainException(
                "catalog.product.tax_rate_not_found",
                "The tax rate was not found in this tenant.");
        }
    }

    private static bool IsProductTaxRateViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException postgres &&
        postgres.SqlState == PostgresErrorCodes.ForeignKeyViolation &&
        string.Equals(
            postgres.ConstraintName,
            ProductTaxRateForeignKey,
            StringComparison.Ordinal);

    // El estado sigue siendo Deleted cuando SaveChanges falla: EF sólo lo pasa a Detached después
    // de un commit exitoso. Si eso cambiara, esta rama dejaría de entrar y el caso volvería a
    // salir con el código de la causa opuesta — por eso CA-CAT-06-08 afirma sobre el código.
    private static bool IsDeletingATaxRate(DbUpdateException exception) =>
        exception.Entries.Any(entry =>
            entry.Entity is TaxRate && entry.State == EntityState.Deleted);
}
