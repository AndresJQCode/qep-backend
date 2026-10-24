using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Modules.Catalog.Application;
using Modules.Catalog.Domain;
using Npgsql;

namespace Modules.Catalog.Infrastructure.Persistence;

internal sealed class CatalogUnitOfWork(CatalogDbContext dbContext, IClock clock) : ICatalogUnitOfWork
{
    // Discriminar por nombre de índice y no sólo por SqlState es deliberado: 23505 sólo dice
    // que se violó algún índice único, y responder catalog.product.code_taken para otro
    // mandaría al llamador a corregir el campo equivocado. Esa es la lección con la que se
    // cerró SDD-CT-06, donde otro índice único reportaba el código de dominio equivocado.
    // Desde las variantes (2026-10-20) el código vive en product_variants; el código de dominio no
    // cambia, porque para quien llena el formulario es el mismo error.
    private const string ProductCodeIndex = "IX_product_variants_tenant_code";
    private const string VariantBarcodeIndex = "IX_product_variants_tenant_barcode";
    private const string TaxRateNameIndex = "IX_tax_rates_tenant_name";
    private const string ProductTaxRateForeignKey = "FK_products_tax_rates_tax_rate_id";
    private const string OptionNameIndex = "IX_option_definitions_tenant_name";
    private const string ProductOptionForeignKey =
        "FK_product_options_option_definitions_option_definition_id";
    private const string VariantOptionValueForeignKey =
        "FK_product_variant_option_values_option_values_option_value_id";

    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        StampChangedOptions();

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
        // Rama propia, por la misma razón que la de abajo: cada índice único devuelve su código.
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException postgres &&
                  postgres.SqlState == PostgresErrorCodes.UniqueViolation &&
                  string.Equals(
                      postgres.ConstraintName,
                      VariantBarcodeIndex,
                      StringComparison.Ordinal))
        {
            throw new CatalogDomainException(
                "catalog.variant.barcode_taken",
                "Another variant in this tenant already uses that barcode.");
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
        // La biblioteca de opciones. Otro índice único, otro código: el nombre de la opción ya existe en
        // el tenant (sin mayúsculas: el índice es sobre lower(name)). Sin consulta previa en el
        // handler; el índice es lo único que cierra la carrera de dos POST a la vez.
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException postgres &&
                  postgres.SqlState == PostgresErrorCodes.UniqueViolation &&
                  string.Equals(
                      postgres.ConstraintName,
                      OptionNameIndex,
                      StringComparison.Ordinal))
        {
            throw new CatalogDomainException(
                "catalog.option.name_taken",
                "Another option in this tenant already uses that name.");
        }
        // Las FK RESTRICT de product_options y product_variant_option_values, sólo cuando lo que
        // se estaba guardando era el borrado de una opción o de un valor: es la carrera entre el
        // «¿está en uso?» del handler y el commit, y sale con el mismo 409 que la consulta. Si la
        // misma FK salta escribiendo un producto (una opción que no existe), ese caso no es éste
        // y no se traduce acá: lo resuelve la validación del producto (ProductVariantReferences).
        catch (DbUpdateException exception)
            when (IsOptionReferenceViolation(exception) && IsDeletingAnOptionOrValue())
        {
            throw new ResourceConflictException(
                "catalog.option.in_use",
                "The option or value is used by at least one variant: deactivate it instead.",
                exception);
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

    public async Task<ICatalogTransaction> BeginTransactionAsync(CancellationToken cancellationToken) =>
        new CatalogTransaction(await dbContext.Database.BeginTransactionAsync(cancellationToken));

    private sealed class CatalogTransaction(IDbContextTransaction transaction) : ICatalogTransaction
    {
        public Task CommitAsync(CancellationToken cancellationToken) => transaction.CommitAsync(cancellationToken);

        public ValueTask DisposeAsync() => transaction.DisposeAsync();
    }

    // Los mutadores de OptionDefinition no reciben la hora (Rename, SetActive, AddValue...), pero
    // todos suben la Version, así que una opción cambiada siempre llega Modified. El sello va acá,
    // una sola vez por guardado, con el reloj del sistema.
    private void StampChangedOptions()
    {
        foreach (var entry in dbContext.ChangeTracker.Entries<OptionDefinition>())
        {
            if (entry.State == EntityState.Modified)
            {
                entry.Property(option => option.UpdatedAt).CurrentValue = clock.UtcNow;
            }
        }
    }

    // Las tres FK que se traducen acá son ON DELETE RESTRICT, y Postgres no responde lo mismo
    // según el lado: insertar o actualizar la fila hija da 23503 (foreign_key_violation), pero
    // borrar la padre que alguien usa da 23001 (restrict_violation). Mirar sólo 23503 deja el
    // borrado —justo la carrera para la que existe el RESTRICT— saliendo como 500. Mismo criterio
    // que CompaniesUnitOfWork. El nombre de la constraint sigue siendo lo que decide.
    private static bool IsForeignKeyOrRestrictViolation(PostgresException postgres) =>
        postgres.SqlState is PostgresErrorCodes.ForeignKeyViolation or PostgresErrorCodes.RestrictViolation;

    private static bool IsOptionReferenceViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException postgres &&
        IsForeignKeyOrRestrictViolation(postgres) &&
        (string.Equals(postgres.ConstraintName, ProductOptionForeignKey, StringComparison.Ordinal) ||
         string.Equals(postgres.ConstraintName, VariantOptionValueForeignKey, StringComparison.Ordinal));

    // Por el ChangeTracker y no por exception.Entries: con lotes, las entradas que EF adjunta
    // dependen de qué sentencia falló, y el valor borrado en cascada puede no estar entre ellas.
    private bool IsDeletingAnOptionOrValue() =>
        dbContext.ChangeTracker.Entries().Any(entry =>
            entry.Entity is OptionDefinition or OptionValue && entry.State == EntityState.Deleted);

    private static bool IsProductTaxRateViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException postgres &&
        IsForeignKeyOrRestrictViolation(postgres) &&
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
