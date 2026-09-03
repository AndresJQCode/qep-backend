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

    // Sale.QuotationId es 1:1 (IX_sales_quotation, unico). Antes esto era imposible de alcanzar
    // porque convertir dejaba la cotizacion en Approved y EnsureConvertibleToSale (entonces
    // Approve()) ya rechazaba una segunda conversion por estado; sin ese estado (QuotationStatus
    // solo tiene Draft/Sent/Voided/Expired), una cotizacion Sent sigue siendo Sent despues de
    // convertirse, asi que un segundo intento de conversion llega hasta aca -- sin traducir,
    // saldria como 500 con el nombre de la constraint adentro.
    private const string SaleQuotationIndex = "IX_sales_quotation";

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
                      SaleQuotationIndex,
                      StringComparison.Ordinal))
        {
            throw new QuotationsDomainException(
                "quotation.quotation.already_converted",
                "This quotation was already converted to a sale.");
        }
    }
}
