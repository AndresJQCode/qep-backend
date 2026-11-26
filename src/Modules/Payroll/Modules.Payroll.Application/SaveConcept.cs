using BuildingBlocks.Application;
using FluentValidation;
using FluentValidation.Results;
using Modules.Payroll.Domain;
using Modules.Tenancy.Application;

namespace Modules.Payroll.Application;

public sealed record CreateConceptCommand(Guid TenantId, Guid CompanyId, ConceptRequest Request) : ICommand<PayrollConceptDto>;

/// <param name="IfMatch">La versión que la pantalla cargó; crudo: el caso de uso lo lee ya autorizado.</param>
public sealed record UpdateConceptCommand(Guid TenantId, Guid CompanyId, Guid ConceptId, ConceptRequest Request, string? IfMatch)
    : ICommand<PayrollConceptDto>;

/// <summary>Lo que comparten crear y editar un concepto propio.</summary>
internal static class ConceptNames
{
    /// <summary>
    /// Un nombre propio igual al de un concepto del sistema del mismo tipo es 409 (se decide antes de guardar: los del sistema no
    /// son filas y el índice único no los ve). El choque con otro propio lo ataja el índice, archivados incluidos.
    /// </summary>
    public static void EnsureNotASystemName(ConceptKind kind, string name)
    {
        var folded = PayrollText.Fold(name);
        if (SystemConcepts.All.Any(system => system.Kind == kind && PayrollText.Fold(system.Name) == folded))
        {
            throw new ResourceConflictException(PayrollErrorCodes.ConceptNameTaken, "Ya existe un concepto con ese nombre.");
        }
    }
}

public sealed class CreateConceptHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    PayrollCompanies companies,
    IPayrollConceptRepository concepts,
    IPayrollUnitOfWork unitOfWork,
    IPayrollAuditPublisher auditPublisher,
    IValidator<ConceptRequest> validator)
    : ICommandHandler<CreateConceptCommand, PayrollConceptDto>
{
    public async Task<PayrollConceptDto> HandleAsync(CreateConceptCommand command, CancellationToken cancellationToken)
    {
        await PayrollAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, cancellationToken, PayrollPermissions.SettingsManage);
        await validator.ValidateAndThrowAsync(command.Request, cancellationToken);
        await companies.RequireAsync(command.TenantId, command.CompanyId, cancellationToken);

        var request = command.Request;
        var kind = Enum.Parse<ConceptKind>(request.Kind!);
        ConceptNames.EnsureNotASystemName(kind, request.Name!);
        var now = (await tenantClock.GetAsync(command.TenantId, cancellationToken)).UtcNow;

        var concept = PayrollConcept.Create(
            PayrollConceptId.New(), command.TenantId, command.CompanyId, kind, request.Name!,
            request.DefaultSalaryConstitutive, request.Multiplier, now);
        concepts.Add(concept);
        PayrollSupport.PublishAudit(
            auditPublisher, executionContext, command.TenantId, PayrollAuditActions.ConceptCreated,
            PayrollAuditActions.Resources.Concept, concept.Id.ToString(), ["kind", "name"], now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return ConceptCatalogSnapshot.ToDto(concept);
    }
}

public sealed class UpdateConceptHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    PayrollCompanies companies,
    IPayrollConceptRepository concepts,
    IPayrollUnitOfWork unitOfWork,
    IPayrollAuditPublisher auditPublisher,
    IValidator<ConceptRequest> validator)
    : ICommandHandler<UpdateConceptCommand, PayrollConceptDto>
{
    public async Task<PayrollConceptDto> HandleAsync(UpdateConceptCommand command, CancellationToken cancellationToken)
    {
        await PayrollAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, cancellationToken, PayrollPermissions.SettingsManage);
        var expectedVersion = PayrollIfMatch.Require(command.IfMatch);
        await validator.ValidateAndThrowAsync(command.Request, cancellationToken);
        await companies.RequireAsync(command.TenantId, command.CompanyId, cancellationToken);

        // Los del sistema no son filas y no se editan: 422 en vez de un 404 que confundiría con un id inventado.
        if (SystemConcepts.Find(command.ConceptId) is not null)
        {
            throw new PayrollDomainException(PayrollErrorCodes.ConceptSystemReadonly, "System concepts cannot be edited.");
        }

        var concept = await concepts.FindAsync(command.TenantId, command.CompanyId, new PayrollConceptId(command.ConceptId), cancellationToken)
            ?? throw PayrollNotFound.Concept(command.ConceptId);

        var request = command.Request;
        var kind = Enum.Parse<ConceptKind>(request.Kind!);
        if (kind != concept.Kind)
        {
            throw new ValidationException([new ValidationFailure("kind", "El tipo de un concepto no se puede cambiar.")]);
        }

        PayrollSupport.EnsureVersion(concept.Version, expectedVersion);
        if (PayrollText.Fold(request.Name) != concept.NormalizedName)
        {
            ConceptNames.EnsureNotASystemName(kind, request.Name!);
        }

        var changed = new List<string>(3);
        PayrollSupport.Diff(changed, "name", concept.Name, request.Name!.Trim());
        PayrollSupport.Diff(changed, "defaultSalaryConstitutive", concept.DefaultSalaryConstitutive, request.DefaultSalaryConstitutive);
        PayrollSupport.Diff(changed, "multiplier", concept.Multiplier, request.Multiplier);

        var now = (await tenantClock.GetAsync(command.TenantId, cancellationToken)).UtcNow;
        concept.Update(request.Name!, request.DefaultSalaryConstitutive, request.Multiplier, now);
        PayrollSupport.PublishAudit(
            auditPublisher, executionContext, command.TenantId, PayrollAuditActions.ConceptUpdated,
            PayrollAuditActions.Resources.Concept, concept.Id.ToString(), changed, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return ConceptCatalogSnapshot.ToDto(concept);
    }
}
