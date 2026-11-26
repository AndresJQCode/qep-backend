using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Automations.Application;
using Modules.Automations.Domain.Flow;
using Modules.Customers.Application;
using Modules.Customers.Domain;
using Modules.Pipeline.Application;
using static Modules.Automations.IntegrationTests.AutomationsApiHarness;

namespace Modules.Automations.IntegrationTests;

/// <summary>Spec 2026-11-19 (chatbot CRM action): the editor brings the tenant's tags and open stages, and a flow can only reference tags
/// and stages of its own tenant (an import from another tenant fails <c>crm.tag_missing</c> until the person picks one).</summary>
public sealed partial class ChatbotsApiTests
{
    private static string CrmFlow(Guid tagId) => $$"""
        { "schema": 1, "start": { "firstNodeId": "crm" }, "variables": [],
          "nodes": [
            { "id": "crm", "kind": "crmAction", "title": "Etiquetar", "operation": "addTag", "tagId": "{{tagId}}", "stageId": null,
              "saveTo": "crm", "outputs": { "ok": "done", "skipped": null, "error": null } },
            { "id": "done", "kind": "handoff", "title": "Asesor", "text": null }
          ] }
        """;

    private static async Task<Guid> SeedCustomerTagAsync(QepApiFactory factory, Guid tenantId, string name)
    {
        using var scope = factory.Services.CreateScope();
        var tag = Tag.Create(TagId.New(), tenantId, name, TagColor.Blue, DateTimeOffset.UtcNow);
        scope.ServiceProvider.GetRequiredService<ICustomerTagRepository>().Add(tag);
        await scope.ServiceProvider.GetRequiredService<ICustomersUnitOfWork>().SaveChangesAsync(Ct);
        return tag.Id.Value;
    }

    [Fact]
    public async Task EditorResponseCarriesTagsAndOpenStagesInOrder()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var other = await RegisterTenantAsync(factory);
        var tagId = await SeedCustomerTagAsync(factory, setup.Tenant.TenantId, "Interesado");
        await SeedCustomerTagAsync(factory, other.TenantId, "Ajena");

        // Pipeline does not come with the signup: no stages, and the flag says why.
        var withoutPipeline = await CreateAsync(setup, "Sin embudo");
        Assert.True(withoutPipeline.Crm.CustomersEnabled);
        Assert.False(withoutPipeline.Crm.PipelineEnabled);
        Assert.Equal([new ChatbotEditorCrmRefDto(tagId, "Interesado")], withoutPipeline.Crm.Tags);
        Assert.Empty(withoutPipeline.Crm.Stages);

        await ExecuteAsync(connectionString,
            """
            INSERT INTO tenancy.tenant_modules (tenant_id, module_key, enabled_at, source)
            VALUES (@tenantId, 'pipeline', now(), 'manual')
            ON CONFLICT (tenant_id, module_key) DO UPDATE SET status = 'active', status_changed_at = now()
            """,
            ("tenantId", setup.Tenant.TenantId));
        var beforeProvisioning = await GetAsync(setup, withoutPipeline.Chatbot.Id);

        // Opening the editor only reads: a pipeline nobody opened yet has no stages to offer, and none is created.
        Assert.True(beforeProvisioning.Crm.PipelineEnabled);
        Assert.Empty(beforeProvisioning.Crm.Stages);
        Assert.Equal(0L, await PipelineCountAsync(connectionString, setup.Tenant.TenantId));

        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IPipelineProvisioner>().EnsureAsync(setup.Tenant.TenantId, Ct);
        }

        var editor = await GetAsync(setup, withoutPipeline.Chatbot.Id);

        // The default pipeline's open stages, in its order; «Ganado» and «Perdido» are closed and never offered.
        Assert.Equal(["Nuevo", "Calificado", "Cotizado", "Negociación"], editor.Crm.Stages.Select(stage => stage.Name));
        Assert.Equal(1L, await PipelineCountAsync(connectionString, setup.Tenant.TenantId));
    }

    private static Task<long> PipelineCountAsync(string connectionString, Guid tenantId) =>
        ScalarAsync<long>(connectionString, "SELECT count(*) FROM pipeline.pipelines WHERE tenant_id = @tenantId", ("tenantId", tenantId));

    /// <summary>Review Focus (tenant isolation): a flow imported from another tenant names a tag this tenant does not have; it is an error
    /// on the block and publishing is refused, until the person picks a tag of this tenant.</summary>
    [Fact]
    public async Task ImportedFlowFromAnotherTenantFailsPublishWithTagMissing()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var other = await RegisterTenantAsync(factory);
        var foreignTag = await SeedCustomerTagAsync(factory, other.TenantId, "Ajena");
        var ownTag = await SeedCustomerTagAsync(factory, setup.Tenant.TenantId, "Interesado");

        var imported = await ImportChatbotAsync(setup.Client, setup.Tenant.TenantId, CrmFlow(foreignTag), "{}", new Dictionary<Guid, Guid>());

        Assert.Contains(imported.Issues, issue => issue is { Code: FlowIssueCodes.CrmTagMissing, Severity: "Error", NodeId: "crm" });

        // The same flow saved in a chatbot with a connection: still refused while the tag is foreign, published once it is its own.
        var editor = await CreateAsync(setup);
        var foreign = await SaveDraftAsync(setup, editor, CrmFlow(foreignTag));
        var foreignSaved = (await foreign.Content.ReadFromJsonAsync<DraftSavedDto>(Ct))!;
        Assert.Contains(foreignSaved.Issues, issue => issue.Code == FlowIssueCodes.CrmTagMissing);
        var rejected = await PublishAsync(setup.Client, setup, editor.Chatbot.Id, foreignSaved.Version, []);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);

        var current = await GetAsync(setup, editor.Chatbot.Id);
        var own = await SaveDraftAsync(setup, current, CrmFlow(ownTag));
        var ownSaved = (await own.Content.ReadFromJsonAsync<DraftSavedDto>(Ct))!;
        Assert.Empty(ownSaved.Issues.Where(issue => issue.Severity == "Error").Select(issue => issue.Code));
        var published = await PublishAsync(setup.Client, setup, editor.Chatbot.Id, ownSaved.Version, []);
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);
    }
}
