using System.Globalization;
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Payroll.Domain;
using Modules.Tenancy.Application;

namespace Modules.Payroll.Application;

public sealed record CreateVacationCommand(Guid TenantId, Guid CompanyId, Guid EmployeeId, VacationRequest Request)
    : ICommand<IReadOnlyList<VacationDto>>;

/// <param name="IfMatch">La versión de las vacaciones que la pantalla cargó; crudo: el caso de uso lo lee ya autorizado.</param>
public sealed record UpdateVacationCommand(
    Guid TenantId, Guid CompanyId, Guid EmployeeId, Guid VacationId, VacationRequest Request, string? IfMatch)
    : ICommand<IReadOnlyList<VacationDto>>;

/// <summary>Lo que comparten crear y editar: de la solicitud ya validada a lo que se guarda.</summary>
internal static class VacationWrites
{
    /// <summary>Los días hábiles que se guardan: los calculados con <c>Qep</c>, los tecleados (los de Siigo) con <c>External</c>.</summary>
    public static int BusinessDaysOf(VacationOrigin origin, VacationRequest request, VacationFacts facts) =>
        origin == VacationOrigin.Qep ? facts.ComputedBusinessDays : request.BusinessDays!.Value;

    /// <summary>Un monto ausente vale 0, salvo el pago de vacaciones, que sigue nulo: entonces el valor del día sale del salario.</summary>
    public static IReadOnlyList<ExternalVacationMonth> MonthsOf(VacationRequest request) =>
        (request.ExternalMonths ?? [])
            .Select(month => new ExternalVacationMonth(
                DateOnly.ParseExact(month.Month! + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture),
                month.VacationPay,
                month.HealthAdvance ?? 0m,
                month.PensionAdvance ?? 0m,
                month.SolidarityFundAdvance ?? 0m))
            .OrderBy(month => month.Month)
            .ToList();

    /// <summary>
    /// El solapamiento con las demás del empleado en la empresa, ignorando <paramref name="ignored"/> (la que se edita). La base lo
    /// frena también (<c>EX_vacations_no_overlap</c>) para dos registros simultáneos que esta lectura no ve.
    /// </summary>
    public static void EnsureNoOverlap(IEnumerable<Vacation> others, VacationId? ignored, DateOnly start, DateOnly end) =>
        PayrollConflicts.Run(() =>
        {
            if (others.Any(other => other.Id != ignored && other.Overlaps(start, end)))
            {
                throw new PayrollDomainException(
                    PayrollErrorCodes.VacationOverlap, "The employee already has vacations that overlap those dates.");
            }
        });
}

public sealed class CreateVacationHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    VacationRules rules,
    IVacationRepository vacations,
    VacationLocks locks,
    VacationComposer composer,
    PeriodActors actors,
    IPayrollUnitOfWork unitOfWork,
    IPayrollAuditPublisher auditPublisher,
    IValidator<VacationRequest> validator)
    : ICommandHandler<CreateVacationCommand, IReadOnlyList<VacationDto>>
{
    public async Task<IReadOnlyList<VacationDto>> HandleAsync(CreateVacationCommand command, CancellationToken cancellationToken)
    {
        await PayrollAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, cancellationToken,
            PayrollPermissions.PeriodRead, PayrollPermissions.PeriodManage);
        await validator.ValidateAndThrowAsync(command.Request, cancellationToken);

        var request = command.Request;
        var origin = Enum.Parse<VacationOrigin>(request.Origin!);
        var start = request.StartDate!.Value;
        var end = request.EndDate!.Value;
        var facts = await rules.CheckAsync(
            command.TenantId, command.CompanyId, command.EmployeeId, origin, start, end, cancellationToken);

        var today = await vacations.ListByEmployeeAsync(command.TenantId, command.CompanyId, command.EmployeeId, cancellationToken);
        VacationWrites.EnsureNoOverlap(today, null, start, end);

        var now = (await tenantClock.GetAsync(command.TenantId, cancellationToken)).UtcNow;
        var created = Vacation.Create(
            VacationId.New(), command.TenantId, command.CompanyId, command.EmployeeId, origin, start, end,
            VacationWrites.BusinessDaysOf(origin, request, facts), facts.ReturnDate, request.PreviousMonthIbc,
            VacationWrites.MonthsOf(request), now);

        await locks.EnsureUnlockedAsync(command.TenantId, command.CompanyId, command.EmployeeId, today, [.. today, created], cancellationToken);
        vacations.Add(created);
        await locks.MarkReachedOutdatedAsync(
            command.TenantId, command.CompanyId, [created], await actors.CurrentAsync(command.TenantId, cancellationToken), now, cancellationToken);

        // Sólo nombres de campo, nunca valores (audit.entries).
        PayrollSupport.PublishAudit(
            auditPublisher, executionContext, command.TenantId, PayrollAuditActions.VacationCreated,
            PayrollAuditActions.Resources.Vacation, created.Id.ToString(),
            ["origin", "startDate", "endDate", "previousMonthIbc", "businessDays", "externalMonths"], now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await composer.ListAsync(command.TenantId, command.CompanyId, command.EmployeeId, cancellationToken);
    }
}

public sealed class UpdateVacationHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    VacationRules rules,
    IVacationRepository vacations,
    VacationLocks locks,
    VacationComposer composer,
    PeriodActors actors,
    IPayrollUnitOfWork unitOfWork,
    IPayrollAuditPublisher auditPublisher,
    IValidator<VacationRequest> validator)
    : ICommandHandler<UpdateVacationCommand, IReadOnlyList<VacationDto>>
{
    public async Task<IReadOnlyList<VacationDto>> HandleAsync(UpdateVacationCommand command, CancellationToken cancellationToken)
    {
        await PayrollAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, cancellationToken,
            PayrollPermissions.PeriodRead, PayrollPermissions.PeriodManage);
        var expectedVersion = PayrollIfMatch.Require(command.IfMatch);
        await validator.ValidateAndThrowAsync(command.Request, cancellationToken);

        // The repository filters by tenant and id only: the company and the employee of the route must be the vacation's own.
        var found = await vacations.FindAsync(command.TenantId, new VacationId(command.VacationId), cancellationToken);
        if (found is null || found.CompanyId != command.CompanyId || found.EmployeeId != command.EmployeeId)
        {
            throw PayrollNotFound.Vacation(command.VacationId);
        }

        PayrollSupport.EnsureVersion(found.Version, expectedVersion);

        var request = command.Request;
        var origin = Enum.Parse<VacationOrigin>(request.Origin!);
        var start = request.StartDate!.Value;
        var end = request.EndDate!.Value;
        var facts = await rules.CheckAsync(
            command.TenantId, command.CompanyId, command.EmployeeId, origin, start, end, cancellationToken);

        var today = await vacations.ListByEmployeeAsync(command.TenantId, command.CompanyId, command.EmployeeId, cancellationToken);
        VacationWrites.EnsureNoOverlap(today, found.Id, start, end);

        var now = (await tenantClock.GetAsync(command.TenantId, cancellationToken)).UtcNow;
        var businessDays = VacationWrites.BusinessDaysOf(origin, request, facts);
        var months = VacationWrites.MonthsOf(request);

        // The vacation as it would be, not saved: the lock plans the quincenas with it before anything is written.
        var replacement = Vacation.Create(
            found.Id, command.TenantId, command.CompanyId, command.EmployeeId, origin, start, end, businessDays, facts.ReturnDate,
            request.PreviousMonthIbc, months, now);
        var after = today.Select(item => item.Id == found.Id ? replacement : item).ToList();
        await locks.EnsureUnlockedAsync(command.TenantId, command.CompanyId, command.EmployeeId, today, after, cancellationToken);

        var changed = new List<string>(6);
        PayrollSupport.Diff(changed, "origin", found.Origin, origin);
        PayrollSupport.Diff(changed, "startDate", found.StartDate, start);
        PayrollSupport.Diff(changed, "endDate", found.EndDate, end);
        PayrollSupport.Diff(changed, "previousMonthIbc", found.PreviousMonthIbc, request.PreviousMonthIbc);
        PayrollSupport.Diff(changed, "businessDays", found.BusinessDays, businessDays);
        if (!found.ExternalMonths.SequenceEqual(months))
        {
            changed.Add("externalMonths");
        }

        // What the old range reached and what the new one reaches both stop being up to date.
        var old = today.Single(item => item.Id == found.Id);
        found.Update(origin, start, end, businessDays, facts.ReturnDate, request.PreviousMonthIbc, months, now);
        await locks.MarkReachedOutdatedAsync(
            command.TenantId, command.CompanyId, [old, replacement], await actors.CurrentAsync(command.TenantId, cancellationToken), now, cancellationToken);

        PayrollSupport.PublishAudit(
            auditPublisher, executionContext, command.TenantId, PayrollAuditActions.VacationUpdated,
            PayrollAuditActions.Resources.Vacation, found.Id.ToString(), changed, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await composer.ListAsync(command.TenantId, command.CompanyId, command.EmployeeId, cancellationToken);
    }
}
