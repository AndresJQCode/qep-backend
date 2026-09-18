using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
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

    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await dbContext.SaveChangesAsync(cancellationToken);
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
    }
}
