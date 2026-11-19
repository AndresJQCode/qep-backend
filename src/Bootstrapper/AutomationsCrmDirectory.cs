using Modules.Automations.Application;
using Modules.Customers.Application;
using Modules.Pipeline.Application;
using Modules.Pipeline.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Bootstrapper;

/// <summary>
/// Spec 2026-11-19 (chatbot CRM action): the tenant's tag catalog and the open stages of its pipeline, for the chatbot editor and the
/// validator. Automations references neither Customers nor Pipeline, so the read goes here, as <see cref="AutomationsAgentDirectory"/>.
/// <para>
/// A module that is off gives an empty list, and so does a tenant missing from Tenancy (the development stub): the engine answers
/// <c>module_off</c> in both cases, so the editor says the same. The pipeline is provisioned on first access, as every Pipeline read does.
/// </para>
/// </summary>
internal sealed class AutomationsCrmDirectory(ITenantModules tenantModules, ICustomerTagRepository tags, IPipelineProvisioner pipelines)
    : IAutomationsCrmDirectory
{
    public async Task<CrmCatalog> ReadAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var modules = await tenantModules.FindAsync(tenantId, cancellationToken);
        var customersEnabled = modules?.IsEnabled(TenantModuleKeys.Customers) ?? false;
        var pipelineEnabled = modules?.IsEnabled(TenantModuleKeys.Pipeline) ?? false;

        IReadOnlyList<CrmTagRef> tagRefs = customersEnabled
            ? [.. (await tags.ListAsync(tenantId, cancellationToken)).Select(entry => new CrmTagRef(entry.Tag.Id.Value, entry.Tag.Name))]
            : [];
        IReadOnlyList<CrmStageRef> stages = pipelineEnabled
            ? [
                .. (await pipelines.EnsureAsync(tenantId, cancellationToken)).OrderedStages
                    .Where(stage => stage.Kind == StageKind.Open)
                    .Select(stage => new CrmStageRef(stage.Id, stage.Name)),
            ]
            : [];
        return new CrmCatalog(customersEnabled, pipelineEnabled, tagRefs, stages);
    }
}
