using BuildingBlocks.Application;
using Modules.Payroll.Domain;
using Modules.Tenancy.Application;

namespace Modules.Payroll.Application;

/// <param name="IfMatch">La versión de las vacaciones que la pantalla cargó; crudo: el caso de uso lo lee ya autorizado.</param>
public sealed record DeleteVacationCommand(Guid TenantId, Guid CompanyId, Guid EmployeeId, Guid VacationId, string? IfMatch)
    : ICommand<IReadOnlyList<VacationDto>>;

/// <summary>Borra unas vacaciones con el mismo bloqueo por huella que editarlas: lo que dejan de leer las quincenas también cuenta.</summary>
public sealed class DeleteVacationHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    PayrollCompanies companies,
    IVacationRepository vacations,
    VacationLocks locks,
    VacationComposer composer,
    PeriodActors actors,
    IPayrollUnitOfWork unitOfWork,
    IPayrollAuditPublisher auditPublisher)
    : ICommandHandler<DeleteVacationCommand, IReadOnlyList<VacationDto>>
{
    public async Task<IReadOnlyList<VacationDto>> HandleAsync(DeleteVacationCommand command, CancellationToken cancellationToken)
    {
        await PayrollAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, cancellationToken,
            PayrollPermissions.PeriodRead, PayrollPermissions.PeriodManage);
        var expectedVersion = PayrollIfMatch.Require(command.IfMatch);

        // The repository filters by tenant and id only: the company and the employee of the route must be the vacation's own.
        var found = await vacations.FindAsync(command.TenantId, new VacationId(command.VacationId), cancellationToken);
        if (found is null || found.CompanyId != command.CompanyId || found.EmployeeId != command.EmployeeId)
        {
            throw PayrollNotFound.Vacation(command.VacationId);
        }

        PayrollSupport.EnsureVersion(found.Version, expectedVersion);
        PayrollCompanies.EnsureActive(
            await companies.RequireAsync(command.TenantId, command.CompanyId, cancellationToken),
            "An inactive company cannot change its vacations.");

        var today = await vacations.ListByEmployeeAsync(command.TenantId, command.CompanyId, command.EmployeeId, cancellationToken);
        await locks.EnsureUnlockedAsync(
            command.TenantId, command.CompanyId, command.EmployeeId, today, today.Where(item => item.Id != found.Id).ToList(), cancellationToken);

        var now = (await tenantClock.GetAsync(command.TenantId, cancellationToken)).UtcNow;
        vacations.Remove(found);
        await locks.MarkReachedOutdatedAsync(
            command.TenantId, command.CompanyId, [found], await actors.CurrentAsync(command.TenantId, cancellationToken), now, cancellationToken);

        PayrollSupport.PublishAudit(
            auditPublisher, executionContext, command.TenantId, PayrollAuditActions.VacationDeleted,
            PayrollAuditActions.Resources.Vacation, found.Id.ToString(), [], now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await composer.ListAsync(command.TenantId, command.CompanyId, command.EmployeeId, cancellationToken);
    }
}
