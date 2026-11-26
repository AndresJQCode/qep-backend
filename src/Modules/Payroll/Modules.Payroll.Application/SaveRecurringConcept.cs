using BuildingBlocks.Application;
using FluentValidation;
using FluentValidation.Results;
using Modules.Payroll.Domain;
using Modules.Tenancy.Application;

namespace Modules.Payroll.Application;

public sealed record CreateRecurringConceptCommand(Guid TenantId, Guid CompanyId, Guid EmployeeId, RecurringConceptRequest Request)
    : ICommand<IReadOnlyList<RecurringConceptDto>>;

/// <param name="IfMatch">La versión del recurrente que la pantalla cargó; crudo: el caso de uso lo lee ya autorizado.</param>
public sealed record UpdateRecurringConceptCommand(
    Guid TenantId, Guid CompanyId, Guid EmployeeId, Guid RecurringId, RecurringConceptRequest Request, string? IfMatch)
    : ICommand<IReadOnlyList<RecurringConceptDto>>;

/// <summary>Lo que comparten crear y editar un recurrente: el concepto contra el catálogo y la clasificación según su tipo.</summary>
internal static class RecurringRules
{
    public static ResolvedConcept ResolveConcept(ConceptCatalogSnapshot catalog, RecurringConceptRequest request)
    {
        var concept = catalog.Find(request.ConceptId!.Value)
            ?? throw new ValidationException([new ValidationFailure("conceptId", "El concepto no existe.")]);

        // Las horas extras entran por la captura de la quincena, nunca como recurrente.
        if (concept.Kind == ConceptKind.Overtime)
        {
            throw new PayrollDomainException(
                PayrollErrorCodes.ConceptKindMismatch, "A recurring concept is an income or a deduction, never overtime.");
        }

        if (concept.Archived)
        {
            throw new PayrollDomainException(PayrollErrorCodes.ConceptArchived, "The concept is archived.");
        }

        if (concept.Kind == ConceptKind.Income && request.SalaryConstitutive is null)
        {
            throw new ValidationException([new ValidationFailure("salaryConstitutive", "Indica si el ingreso constituye salario.")]);
        }

        if (concept.Kind == ConceptKind.Deduction && request.SalaryConstitutive is not null)
        {
            throw new ValidationException([new ValidationFailure("salaryConstitutive", "Una deducción no lleva clasificación.")]);
        }

        return concept;
    }
}

public sealed class CreateRecurringConceptHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    PayrollCompanies companies,
    PayrollEmployees employees,
    ConceptCatalog catalog,
    IRecurringConceptRepository recurring,
    RecurringConceptComposer composer,
    IPayrollUnitOfWork unitOfWork,
    IPayrollAuditPublisher auditPublisher,
    IValidator<RecurringConceptRequest> validator)
    : ICommandHandler<CreateRecurringConceptCommand, IReadOnlyList<RecurringConceptDto>>
{
    public async Task<IReadOnlyList<RecurringConceptDto>> HandleAsync(
        CreateRecurringConceptCommand command, CancellationToken cancellationToken)
    {
        // Sólo `settings.manage` (decisión del owner): un rol de configuración usa la ficha sin leer períodos.
        await PayrollAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, cancellationToken, PayrollPermissions.SettingsManage);
        await validator.ValidateAndThrowAsync(command.Request, cancellationToken);
        await companies.RequireAsync(command.TenantId, command.CompanyId, cancellationToken);
        await employees.RequireInCompanyAsync(command.TenantId, command.CompanyId, command.EmployeeId, cancellationToken);

        var request = command.Request;
        var concept = RecurringRules.ResolveConcept(
            await catalog.LoadAsync(command.TenantId, command.CompanyId, cancellationToken), request);
        var now = (await tenantClock.GetAsync(command.TenantId, cancellationToken)).UtcNow;

        var created = RecurringConcept.Create(
            RecurringConceptId.New(),
            command.TenantId,
            command.CompanyId,
            command.EmployeeId,
            concept.Id,
            Enum.Parse<RecurringMode>(request.Mode!),
            request.Amount!.Value,
            concept.Kind == ConceptKind.Income ? request.SalaryConstitutive : null,
            request.Cap,
            now);
        recurring.Add(created);
        PayrollSupport.PublishAudit(
            auditPublisher, executionContext, command.TenantId, PayrollAuditActions.RecurringCreated,
            PayrollAuditActions.Resources.Recurring, created.Id.ToString(), ["conceptId", "mode", "amount", "salaryConstitutive", "cap"], now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await composer.ListAsync(command.TenantId, command.CompanyId, command.EmployeeId, cancellationToken);
    }
}

public sealed class UpdateRecurringConceptHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    PayrollCompanies companies,
    PayrollEmployees employees,
    ConceptCatalog catalog,
    IRecurringConceptRepository recurring,
    RecurringConceptComposer composer,
    IPayrollUnitOfWork unitOfWork,
    IPayrollAuditPublisher auditPublisher,
    IValidator<RecurringConceptRequest> validator)
    : ICommandHandler<UpdateRecurringConceptCommand, IReadOnlyList<RecurringConceptDto>>
{
    public async Task<IReadOnlyList<RecurringConceptDto>> HandleAsync(
        UpdateRecurringConceptCommand command, CancellationToken cancellationToken)
    {
        await PayrollAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, cancellationToken, PayrollPermissions.SettingsManage);
        var expectedVersion = PayrollIfMatch.Require(command.IfMatch);
        await validator.ValidateAndThrowAsync(command.Request, cancellationToken);
        await companies.RequireAsync(command.TenantId, command.CompanyId, cancellationToken);
        await employees.RequireInCompanyAsync(command.TenantId, command.CompanyId, command.EmployeeId, cancellationToken);

        var found = await recurring.FindAsync(command.TenantId, new RecurringConceptId(command.RecurringId), cancellationToken);
        if (found is null || found.CompanyId != command.CompanyId || found.EmployeeId != command.EmployeeId)
        {
            throw PayrollNotFound.Recurring(command.RecurringId);
        }

        var request = command.Request;
        PayrollSupport.EnsureVersion(found.Version, expectedVersion);

        // El concepto de un recurrente no se cambia (los acumulados son por recurrente): se crea otro.
        if (request.ConceptId != found.ConceptId)
        {
            throw new ValidationException([new ValidationFailure("conceptId", "El concepto de un recurrente no se cambia: crea otro.")]);
        }

        var concept = RecurringRules.ResolveConcept(
            await catalog.LoadAsync(command.TenantId, command.CompanyId, cancellationToken), request);
        var mode = Enum.Parse<RecurringMode>(request.Mode!);
        var salaryConstitutive = concept.Kind == ConceptKind.Income ? request.SalaryConstitutive : null;

        var changed = new List<string>(4);
        PayrollSupport.Diff(changed, "mode", found.Mode, mode);
        PayrollSupport.Diff(changed, "amount", found.Amount, request.Amount!.Value);
        PayrollSupport.Diff(changed, "salaryConstitutive", found.SalaryConstitutive, salaryConstitutive);
        PayrollSupport.Diff(changed, "cap", found.Cap, request.Cap);

        var now = (await tenantClock.GetAsync(command.TenantId, cancellationToken)).UtcNow;
        found.Update(mode, request.Amount!.Value, salaryConstitutive, request.Cap, now);
        PayrollSupport.PublishAudit(
            auditPublisher, executionContext, command.TenantId, PayrollAuditActions.RecurringUpdated,
            PayrollAuditActions.Resources.Recurring, found.Id.ToString(), changed, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await composer.ListAsync(command.TenantId, command.CompanyId, command.EmployeeId, cancellationToken);
    }
}
