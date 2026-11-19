using Modules.Automations.Application;
using Modules.Customers.Application;
using Modules.Pipeline.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Bootstrapper;

/// <summary>
/// Spec 2026-11-19 (chatbot CRM action): the tenant's tag catalog and the open stages of its pipeline, for the chatbot editor and the
/// validator. Automations references neither Customers nor Pipeline, so the read goes here, as <see cref="AutomationsAgentDirectory"/>.
/// <para>
/// A module that is off gives an empty list, and so does a tenant missing from Tenancy (the development stub): the engine answers
/// <c>module_off</c> in both cases, so the editor says the same. Both reads are plain: the tags without counting customers, and the
/// stages without provisioning the pipeline (one nobody opened yet has none to offer, and opening the editor inserts nothing).
/// </para>
/// </summary>
internal sealed class AutomationsCrmDirectory(ITenantModules tenantModules, ICustomerTagLookup tags, IPipelineStageLookup stages)
    : IAutomationsCrmDirectory
{
    public async Task<CrmCatalog> ReadAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var modules = await tenantModules.FindAsync(tenantId, cancellationToken);
        var customersEnabled = modules?.IsEnabled(TenantModuleKeys.Customers) ?? false;
        var pipelineEnabled = modules?.IsEnabled(TenantModuleKeys.Pipeline) ?? false;

        IReadOnlyList<CrmTagRef> tagRefs = customersEnabled
            ? [.. (await tags.ListAllAsync(tenantId, cancellationToken)).Select(tag => new CrmTagRef(tag.Id, tag.Name))]
            : [];
        IReadOnlyList<CrmStageRef> openStages = pipelineEnabled
            ? [.. (await stages.ListOpenAsync(tenantId, cancellationToken)).Select(stage => new CrmStageRef(stage.Id, stage.Name))]
            : [];
        return new CrmCatalog(customersEnabled, pipelineEnabled, tagRefs, openStages);
    }
}
