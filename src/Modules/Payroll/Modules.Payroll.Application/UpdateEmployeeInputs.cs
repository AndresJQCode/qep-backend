using BuildingBlocks.Application;
using FluentValidation;
using FluentValidation.Results;
using Modules.Payroll.Domain;
using Modules.Tenancy.Application;

namespace Modules.Payroll.Application;

/// <param name="IfMatch">La versión del período que la pantalla cargó; crudo: el caso de uso lo lee ya autorizado.</param>
public sealed record UpdateEmployeeInputsCommand(
    Guid TenantId, Guid PeriodId, Guid EmployeeId, EmployeeInputsRequest Request, string? IfMatch)
    : ICommand<PayrollEmployeeDetailDto>;

/// <summary>
/// Reemplaza las tres listas de un empleado en la quincena. Orden: autorizar, <c>If-Match</c>, forma del cuerpo, período del
/// tenant, versión (412), estado editable, empleado en la quincena (404), liquidable (422), conceptos contra el catálogo (422).
/// </summary>
public sealed class UpdateEmployeeInputsHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    IPayrollPeriodRepository periods,
    IPayrollHrDirectory hr,
    IPayrollLegalValues legalValues,
    ConceptCatalog catalog,
    PeriodActors actors,
    PeriodComposer composer,
    IPayrollUnitOfWork unitOfWork,
    IPayrollAuditPublisher auditPublisher,
    IValidator<EmployeeInputsRequest> validator)
    : ICommandHandler<UpdateEmployeeInputsCommand, PayrollEmployeeDetailDto>
{
    public async Task<PayrollEmployeeDetailDto> HandleAsync(UpdateEmployeeInputsCommand command, CancellationToken cancellationToken)
    {
        await PayrollAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, cancellationToken,
            PayrollPermissions.PeriodRead, PayrollPermissions.PeriodManage);
        var expectedVersion = PayrollIfMatch.Require(command.IfMatch);
        await validator.ValidateAndThrowAsync(command.Request, cancellationToken);

        var period = await periods.FindAsync(command.TenantId, new PayrollPeriodId(command.PeriodId), cancellationToken)
            ?? throw PayrollNotFound.Period(command.PeriodId);
        PayrollSupport.EnsureVersion(period.Version, expectedVersion);
        period.EnsureEditable();

        var smmlv = (await PayrollResultMapper.LegalOfAsync(period, legalValues, cancellationToken)).Smmlv.Value;
        var employees = await hr.ListEmployeesAsync(
            command.TenantId, period.CompanyId, period.StartDate, period.EndDate, cancellationToken);
        var employee = employees.FirstOrDefault(candidate => candidate.EmployeeId == command.EmployeeId)
            ?? throw PayrollNotFound.EmployeeNotInPeriod(command.EmployeeId);
        if (PayrollEligibility.Evaluate(employee, period.StartDate, period.EndDate, smmlv).Status == EligibilityStatus.Excluded)
        {
            throw new PayrollDomainException(
                PayrollErrorCodes.EmployeeNotLiquidable, "The employee is not liquidated in this period, so nothing can be captured.");
        }

        var concepts = await catalog.LoadAsync(command.TenantId, period.CompanyId, cancellationToken);
        var quota = ArchivedLineQuota.Of(period.Inputs, command.EmployeeId);
        var failures = new List<ValidationFailure>();
        var request = command.Request;
        var drafts = new List<PeriodInputDraft>(request.Overtime!.Count + request.Incomes!.Count + request.Deductions!.Count);

        for (var index = 0; index < request.Overtime.Count; index++)
        {
            var line = request.Overtime[index];
            if (CheckConcept(concepts, line.ConceptId!.Value, ConceptKind.Overtime, quota, $"overtime[{index}].conceptId", failures))
            {
                drafts.Add(new PeriodInputDraft(line.ConceptId.Value, ConceptKind.Overtime, line.Hours, null, null));
            }
        }

        for (var index = 0; index < request.Incomes.Count; index++)
        {
            var line = request.Incomes[index];
            if (CheckConcept(concepts, line.ConceptId!.Value, ConceptKind.Income, quota, $"incomes[{index}].conceptId", failures))
            {
                drafts.Add(new PeriodInputDraft(line.ConceptId.Value, ConceptKind.Income, null, line.Amount, line.SalaryConstitutive));
            }
        }

        for (var index = 0; index < request.Deductions.Count; index++)
        {
            var line = request.Deductions[index];
            if (CheckConcept(concepts, line.ConceptId!.Value, ConceptKind.Deduction, quota, $"deductions[{index}].conceptId", failures))
            {
                drafts.Add(new PeriodInputDraft(line.ConceptId.Value, ConceptKind.Deduction, null, line.Amount, null));
            }
        }

        if (failures.Count > 0)
        {
            throw new ValidationException(failures);
        }

        var actor = await actors.CurrentAsync(command.TenantId, cancellationToken);
        var now = (await tenantClock.GetAsync(command.TenantId, cancellationToken)).UtcNow;
        period.ReplaceInputs(command.EmployeeId, drafts, actor, now);

        // Sólo nombres de campo, nunca valores (audit.entries).
        PayrollSupport.PublishAudit(
            auditPublisher, executionContext, command.TenantId, PayrollAuditActions.InputsUpdated,
            PayrollAuditActions.Resources.Period, period.Id.ToString(), ["overtime", "incomes", "deductions"], now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await composer.ComposeEmployeeAsync(
            period, command.EmployeeId, actor, actors.CallerPermissions(), cancellationToken);
    }

    // Un concepto que no existe (o es de otra empresa), es de otro tipo o está archivado en una línea nueva es 422 en el campo de su
    // línea; una línea archivada que ya estaba guardada se acepta (decisión del owner 5).
    private static bool CheckConcept(
        ConceptCatalogSnapshot concepts, Guid conceptId, ConceptKind kind, ArchivedLineQuota quota, string field, List<ValidationFailure> failures)
    {
        var concept = concepts.Find(conceptId);
        if (concept is null)
        {
            failures.Add(new ValidationFailure(field, "El concepto no existe."));
        }
        else if (concept.Archived && !quota.TryTake(conceptId, kind))
        {
            failures.Add(new ValidationFailure(field, "El concepto está archivado."));
        }
        else if (concept.Kind != kind)
        {
            failures.Add(new ValidationFailure(field, "El concepto no es de este tipo."));
        }
        else
        {
            return true;
        }

        return false;
    }
}
