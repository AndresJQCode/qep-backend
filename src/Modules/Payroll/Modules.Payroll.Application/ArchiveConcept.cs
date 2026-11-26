using BuildingBlocks.Application;
using Modules.Payroll.Domain;
using Modules.Tenancy.Application;

namespace Modules.Payroll.Application;

/// <summary>Archiva (<c>Archive</c>) o desarchiva un concepto propio. Sin <c>If-Match</c>: un cambio de estado idempotente (I-8).</summary>
public sealed record ArchiveConceptCommand(Guid TenantId, Guid CompanyId, Guid ConceptId, bool Archive) : ICommand<PayrollConceptDto>;

/// <summary>
/// Archivar dos veces o desarchivar uno activo es 409; un concepto del sistema es 422 <c>system_readonly</c>. Desarchivar nunca choca
/// por nombre: la unicidad incluye a los archivados.
/// </summary>
public sealed class ArchiveConceptHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    PayrollCompanies companies,
    IPayrollConceptRepository concepts,
    IPayrollUnitOfWork unitOfWork,
    IPayrollAuditPublisher auditPublisher)
    : ICommandHandler<ArchiveConceptCommand, PayrollConceptDto>
{
    public async Task<PayrollConceptDto> HandleAsync(ArchiveConceptCommand command, CancellationToken cancellationToken)
    {
        await PayrollAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, cancellationToken, PayrollPermissions.SettingsManage);
        await companies.RequireAsync(command.TenantId, command.CompanyId, cancellationToken);

        if (SystemConcepts.Find(command.ConceptId) is not null)
        {
            throw new PayrollDomainException(PayrollErrorCodes.ConceptSystemReadonly, "System concepts cannot be archived.");
        }

        var concept = await concepts.FindAsync(command.TenantId, command.CompanyId, new PayrollConceptId(command.ConceptId), cancellationToken)
            ?? throw PayrollNotFound.Concept(command.ConceptId);
        var now = (await tenantClock.GetAsync(command.TenantId, cancellationToken)).UtcNow;

        PayrollConflicts.Run(() =>
        {
            if (command.Archive)
            {
                concept.Archive(now);
            }
            else
            {
                concept.Unarchive(now);
            }
        });

        PayrollSupport.PublishAudit(
            auditPublisher, executionContext, command.TenantId,
            command.Archive ? PayrollAuditActions.ConceptArchived : PayrollAuditActions.ConceptUnarchived,
            PayrollAuditActions.Resources.Concept, concept.Id.ToString(), ["archived"], now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return ConceptCatalogSnapshot.ToDto(concept);
    }
}
