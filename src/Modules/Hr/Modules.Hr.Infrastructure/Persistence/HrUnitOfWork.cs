using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Modules.Hr.Application;
using Modules.Hr.Domain;
using Npgsql;

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

    // Las FK que apuntan a una configuración (un centro a su sede, un registro de cargo y lugar a su cargo, área, sede o centro). Todas
    // son RESTRICT: borrar algo que ya se usa la rompe.
    private static readonly string[] SettingReferenceForeignKeys =
    [
        "FK_workplaces_sites_site_id",
        "FK_assignment_records_areas_area_id",
        "FK_assignment_records_positions_position_id",
        "FK_assignment_records_sites_site_id",
        "FK_assignment_records_workplaces_workplace_id",
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
        // El caso de uso revisa «en uso» antes de borrar, pero entre esa revisión y el guardado puede llegar una contratación o un
        // centro nuevo: el RESTRICT de la base lo ataja y se responde igual que la revisión, 409. Sólo si lo que se guardaba era el
        // borrado de una configuración: la misma FK rota por un alta que apunta a algo ya borrado sí es un error.
        catch (DbUpdateException exception) when (IsSettingStillReferenced(exception))
        {
            throw new ResourceConflictException(
                HrErrorCodes.SettingsInUse, "Ya se está usando: no se puede eliminar. Archívalo en su lugar.", exception);
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
        // Two writers recording the same exam at once: the use case checks first, the index decides. 422 like the pre-check, not 409
        // (spec 2026-11-23 §7.3).
        catch (DbUpdateException exception) when (UniqueViolation.Is(exception, HrDbContext.MedicalExamUniqueIndex))
        {
            throw new HrDomainException(
                HrErrorCodes.MedicalExamDuplicate, "Ya hay un examen de ese tipo registrado para ese empleado en esa fecha.");
        }
    }

    private bool IsSettingStillReferenced(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.RestrictViolation or PostgresErrorCodes.ForeignKeyViolation,
        } postgres
        && SettingReferenceForeignKeys.Contains(postgres.ConstraintName, StringComparer.Ordinal)
        && dbContext.ChangeTracker.Entries<OrganizationSetting>().Any(entry => entry.State == EntityState.Deleted);
}
