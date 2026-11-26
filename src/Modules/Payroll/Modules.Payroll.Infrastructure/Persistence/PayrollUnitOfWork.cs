using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Modules.Payroll.Application;
using Modules.Payroll.Domain;

namespace Modules.Payroll.Infrastructure.Persistence;

internal sealed class PayrollUnitOfWork(PayrollDbContext dbContext) : IPayrollUnitOfWork
{
    private const string ConceptNameIndex = "IX_concepts_tenant_company_kind_normalized_name";
    private const string CompanySettingsIndex = "IX_company_settings_tenant_company";
    private const string PeriodStartIndex = "IX_periods_tenant_company_start";
    private const string VacationOverlapConstraint = "EX_vacations_no_overlap";

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        // Traducido acá y no en Application, que no referencia EF Core. La versión del período (y la de la configuración) es el
        // token de concurrencia de toda escritura (412).
        catch (DbUpdateConcurrencyException exception)
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict",
                "The payroll record changed while the update was being committed.",
                exception);
        }
        // El nombre de un concepto es único por empresa y tipo, archivados incluidos: se discrimina por nombre de índice y no sólo
        // por 23505, porque cada índice es una deduplicación distinta.
        catch (DbUpdateException exception) when (UniqueViolation.Is(exception, ConceptNameIndex))
        {
            throw new ResourceConflictException(
                PayrollErrorCodes.ConceptNameTaken, "Ya existe un concepto con ese nombre.", exception);
        }
        // Dos primeras escrituras de la configuración a la vez (R-17): la que llega segunda cargó «sin configuración» y ya no es cierto.
        catch (DbUpdateException exception) when (UniqueViolation.Is(exception, CompanySettingsIndex))
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict",
                "The company payroll settings were saved by someone else.",
                exception);
        }
        // Dos ensure-current a la vez abren la misma quincena. Es un código interno: no sale por HTTP, lo atrapa EnsureCurrentPeriods.
        catch (DbUpdateException exception) when (UniqueViolation.Is(exception, PeriodStartIndex))
        {
            throw new ResourceConflictException(
                PayrollErrorCodes.PeriodAlreadyExists, "That payroll period already exists.", exception);
        }
        // Dos registros que se solapan a la vez: la validación del handler no ve el insert concurrente y la restricción EXCLUDE sí.
        catch (DbUpdateException exception) when (UniqueViolation.IsExclusion(exception, VacationOverlapConstraint))
        {
            throw new ResourceConflictException(
                PayrollErrorCodes.VacationOverlap, "Se cruzan con otras vacaciones de este empleado.", exception);
        }
    }
}
