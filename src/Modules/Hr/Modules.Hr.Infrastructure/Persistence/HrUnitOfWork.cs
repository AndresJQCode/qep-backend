using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Modules.Hr.Application;
using Modules.Hr.Domain;

namespace Modules.Hr.Infrastructure.Persistence;

internal sealed class HrUnitOfWork(HrDbContext dbContext) : IHrUnitOfWork
{
    // Los cuatro únicos de nombre de la configuración (AddHrEmployees): se discrimina por nombre de índice y no sólo por 23505,
    // porque cada uno es una deduplicación distinta y otra violación única sí sería un error.
    private static readonly string[] SettingNameIndexes =
    [
        "IX_sites_tenant_company_normalized_name",
        "IX_workplaces_tenant_site_normalized_name",
        "IX_areas_tenant_normalized_name",
        "IX_positions_tenant_normalized_name",
    ];

    private const string DocumentIndex = "IX_employees_tenant_document_number";
    private const string MemberIndex = "IX_employees_tenant_member_id";

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        // Traducido acá y no en Application, que no referencia EF Core. `employee.version` es el token de concurrencia de toda
        // escritura sobre un empleado (412), y las de configuración tienen el suyo.
        catch (DbUpdateConcurrencyException exception)
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict",
                "The hr record changed while the update was being committed.",
                exception);
        }
        catch (DbUpdateException exception) when (UniqueViolation.Is(exception, SettingNameIndexes))
        {
            throw new ResourceConflictException(HrErrorCodes.SettingsNameTaken, "Ya existe uno con ese nombre.", exception);
        }
        catch (DbUpdateException exception) when (UniqueViolation.Is(exception, DocumentIndex))
        {
            throw new ResourceConflictException(HrErrorCodes.DocumentTaken, "Ya hay un empleado con ese documento.", exception);
        }
        catch (DbUpdateException exception) when (UniqueViolation.Is(exception, MemberIndex))
        {
            throw new ResourceConflictException(HrErrorCodes.MemberTaken, "Esa persona ya está vinculada a otro empleado.", exception);
        }
    }
}
