using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Modules.Customers.Application;
using Modules.Customers.Domain;
using Modules.Pipeline.Application;
using Modules.Pipeline.Domain;
using static Modules.Automations.IntegrationTests.AutomationsApiHarness;

namespace Modules.Automations.IntegrationTests;

/// <summary>Spec 2026-11-19 (chatbot CRM action) end to end: an inbound walks into the «Acción de CRM» block, the engine acts on Customers or
/// Pipeline as the system through the Bootstrapper adapter, and the walk leaves by the output the result maps to.</summary>
public sealed partial class EngineEndToEndTests
{
    /// <summary>The block, then one Mensaje per output naming the result, then resolve.</summary>
    private static string CrmFlowJson(string operation, Guid? tagId, Guid? stageId)
    {
        var tag = tagId is null ? "null" : $"\"{tagId}\"";
        var stage = stageId is null ? "null" : $"\"{stageId}\"";
        return $$"""
            {
              "schema": 1,
              "start": { "firstNodeId": "crm" },
              "variables": [],
              "nodes": [
                { "id": "crm", "kind": "crmAction", "title": "Acción de CRM", "operation": "{{operation}}", "tagId": {{tag}}, "stageId": {{stage}},
                  "saveTo": "crm", "outputs": { "ok": "ok", "skipped": "skipped", "error": "error" } },
                { "id": "ok", "kind": "message", "title": "Listo", "text": "ok {crm.result}", "outputs": { "next": "end" } },
                { "id": "skipped", "kind": "message", "title": "Nada", "text": "skipped {crm.result}", "outputs": { "next": "end" } },
                { "id": "error", "kind": "message", "title": "Error", "text": "error {crm.result}", "outputs": { "next": "end" } },
                { "id": "end", "kind": "resolve", "title": "Listo" }
              ]
            }
            """;
    }

    private static async Task<Guid> SeedTagAsync(WebApplicationFactory<Program> host, Guid tenantId, string name)
    {
        using var scope = host.Services.CreateScope();
        var tag = Tag.Create(TagId.New(), tenantId, name, TagColor.Blue, DateTimeOffset.UtcNow);
        scope.ServiceProvider.GetRequiredService<ICustomerTagRepository>().Add(tag);
        await scope.ServiceProvider.GetRequiredService<ICustomersUnitOfWork>().SaveChangesAsync(Ct);
        return tag.Id.Value;
    }

    private static async Task<SalesPipeline> EnablePipelineAsync(WebApplicationFactory<Program> host, string connectionString, Guid tenantId)
    {
        await ExecuteAsync(connectionString,
            """
            INSERT INTO tenancy.tenant_modules (tenant_id, module_key, enabled_at, source)
            VALUES (@tenantId, 'pipeline', now(), 'manual')
            ON CONFLICT (tenant_id, module_key) DO UPDATE SET status = 'active', status_changed_at = now()
            """,
            ("tenantId", tenantId));
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IPipelineProvisioner>().EnsureAsync(tenantId, Ct);
    }

    /// <summary>An open deal of <paramref name="customerId"/> in the first open stage; from the conversation when <paramref name="conversationId"/>
    /// is given.</summary>
    private static async Task<Guid> SeedDealAsync(Fixture f, Guid customerId, Guid? conversationId)
    {
        var owner = await OwnerMembershipIdAsync(f.ConnectionString, f.Tenant);
        using var scope = f.Host.Services.CreateScope();
        var pipeline = await scope.ServiceProvider.GetRequiredService<IPipelineProvisioner>().EnsureAsync(f.Tenant.TenantId, Ct);
        var deal = Deal.Create(
            f.Tenant.TenantId, pipeline, customerId, owner, "Negocio", null, "COP", null,
            conversationId is null ? DealSource.Manual : DealSource.Conversation, conversationId, DealActor.ForUser(owner), DateTimeOffset.UtcNow);
        scope.ServiceProvider.GetRequiredService<IDealRepository>().Add(deal);
        await scope.ServiceProvider.GetRequiredService<IPipelineUnitOfWork>().SaveChangesAsync(Ct);
        return deal.Id;
    }

    /// <summary>The conversation's customer, linked by the ingestion when the inbound arrived.</summary>
    private static Task<Guid> ConversationCustomerAsync(Fixture f) =>
        ScalarAsync<Guid>(f.ConnectionString, "SELECT customer_id FROM messaging.conversations");

    [Fact]
    public async Task TaggingFlowTagsTheCustomerAndLeavesByOk()
    {
        await using var database = await StartDatabaseAsync();
        var tagId = Guid.Empty;
        using var f = await ArrangeAsync(database, flowOf: async (host, tenant) =>
        {
            tagId = await SeedTagAsync(host, tenant.TenantId, "Interesado");
            return CrmFlowJson("addTag", tagId, null);
        });

        await InboundAsync(f, "w1", "Hola");
        await DrainAutomationsAsync(f.Host);

        Assert.Equal(["ok added"], f.Meta.Sends());
        var customerId = await ConversationCustomerAsync(f);
        Assert.Equal(1L, await CountAsync(f.ConnectionString,
            "SELECT count(*) FROM customers.customer_tags WHERE customer_id = @c AND tag_id = @t", ("c", customerId), ("t", tagId)));
        // Customers' audit travels through the platform outbox: the system, on behalf of the chatbot.
        Assert.Equal(1L, await CountAsync(f.ConnectionString,
            "SELECT count(*) FROM platform.outbox_messages WHERE event_name = 'platform.audit.recorded.v1' "
            + "AND payload->>'action' = 'customers.customer.tags_changed' AND payload->>'actorType' = 'System' AND payload->>'source' = 'chatbot'"));
        Assert.Equal(1L, await AuditAsync(f, "automations.run.crm_action_applied"));
    }

    [Fact]
    public async Task MoveDealMovesTheDealFromTheConversation()
    {
        await using var database = await StartDatabaseAsync();
        var target = Guid.Empty;
        using var f = await ArrangeAsync(database, flowOf: async (host, tenant) =>
        {
            var pipeline = await EnablePipelineAsync(host, database.GetConnectionString(), tenant.TenantId);
            target = pipeline.OrderedStages.Where(stage => stage.Kind == StageKind.Open).Skip(1).First().Id;
            return CrmFlowJson("moveDeal", null, target);
        });

        // The inbound is ingested (conversation and customer) before the engine runs, so the deal can be born from it in between.
        await InboundAsync(f, "w1", "Hola");
        var customerId = await ConversationCustomerAsync(f);
        var conversationId = await ScalarAsync<Guid>(f.ConnectionString, "SELECT id FROM messaging.conversations");
        var dealId = await SeedDealAsync(f, customerId, conversationId);
        await DrainAutomationsAsync(f.Host);

        Assert.Equal(["ok moved"], f.Meta.Sends());
        Assert.Equal(target, await ScalarAsync<Guid>(f.ConnectionString, "SELECT stage_id FROM pipeline.deals WHERE id = @id", ("id", dealId)));
        Assert.Equal(1L, await CountAsync(f.ConnectionString,
            "SELECT count(*) FROM audit.entries WHERE action = 'pipeline.deal.moved' AND actor_type = 'System' AND source = 'chatbot' AND resource_id = @d",
            ("d", dealId.ToString())));
        Assert.Equal(1L, await AuditAsync(f, "automations.run.crm_action_applied"));
    }

    /// <summary>Review Focus 1: a tag deleted after the flow was published is the block's <c>error</c> exit with <c>tag_missing</c>, not an
    /// exception that fails the run.</summary>
    [Fact]
    public async Task DeletedTagAfterPublishLeavesByError()
    {
        await using var database = await StartDatabaseAsync();
        var tagId = Guid.Empty;
        using var f = await ArrangeAsync(database, flowOf: async (host, tenant) =>
        {
            tagId = await SeedTagAsync(host, tenant.TenantId, "Interesado");
            return CrmFlowJson("addTag", tagId, null);
        });
        await ExecuteAsync(f.ConnectionString, "DELETE FROM customers.tags WHERE id = @id", ("id", tagId));

        await InboundAsync(f, "w1", "Hola");
        await DrainAutomationsAsync(f.Host);

        Assert.Equal(["error tag_missing"], f.Meta.Sends());
        Assert.Equal(string.Empty, await RunAsync(f, "coalesce(failure_code, '')"));
        Assert.Equal(1L, await AuditAsync(f, "automations.run.crm_action_failed"));
    }

    /// <summary>Review Focus 2: a customer already at <see cref="CustomerTagAssignment.MaxPerCustomer"/> tags is not tagged; the block leaves
    /// by <c>error</c> with <c>tag_limit</c>.</summary>
    [Fact]
    public async Task LimitLeavesByErrorWithTagLimit()
    {
        await using var database = await StartDatabaseAsync();
        var tagId = Guid.Empty;
        using var f = await ArrangeAsync(database, flowOf: async (host, tenant) =>
        {
            tagId = await SeedTagAsync(host, tenant.TenantId, "Interesado");
            return CrmFlowJson("addTag", tagId, null);
        });

        await InboundAsync(f, "w1", "Hola");
        var customerId = await ConversationCustomerAsync(f);
        using (var scope = f.Host.Services.CreateScope())
        {
            var tags = scope.ServiceProvider.GetRequiredService<ICustomerTagRepository>();
            for (var i = 0; i < CustomerTagAssignment.MaxPerCustomer; i++)
            {
                var other = Tag.Create(TagId.New(), f.Tenant.TenantId, $"Etiqueta {i}", TagColor.Blue, DateTimeOffset.UtcNow);
                tags.Add(other);
                tags.AddAssignment(CustomerTagAssignment.Create(
                    new CustomerId(customerId), other.Id, f.Tenant.TenantId, f.Tenant.OwnerUserId, DateTimeOffset.UtcNow));
            }

            await scope.ServiceProvider.GetRequiredService<ICustomersUnitOfWork>().SaveChangesAsync(Ct);
        }

        await DrainAutomationsAsync(f.Host);

        Assert.Equal(["error tag_limit"], f.Meta.Sends());
        Assert.Equal(0L, await CountAsync(f.ConnectionString,
            "SELECT count(*) FROM customers.customer_tags WHERE tag_id = @t", ("t", tagId)));
        Assert.Equal((long)CustomerTagAssignment.MaxPerCustomer, await CountAsync(f.ConnectionString,
            "SELECT count(*) FROM customers.customer_tags WHERE customer_id = @c", ("c", customerId)));
    }

    /// <summary>Review Focus 5: two open deals of the customer and none from this conversation is never a guess: <c>skipped</c>.</summary>
    [Fact]
    public async Task SeveralOpenDealsLeavesBySkipped()
    {
        await using var database = await StartDatabaseAsync();
        var target = Guid.Empty;
        using var f = await ArrangeAsync(database, flowOf: async (host, tenant) =>
        {
            var pipeline = await EnablePipelineAsync(host, database.GetConnectionString(), tenant.TenantId);
            target = pipeline.OrderedStages.Where(stage => stage.Kind == StageKind.Open).Skip(1).First().Id;
            return CrmFlowJson("moveDeal", null, target);
        });

        await InboundAsync(f, "w1", "Hola");
        var customerId = await ConversationCustomerAsync(f);
        await SeedDealAsync(f, customerId, null);
        await SeedDealAsync(f, customerId, null);
        await DrainAutomationsAsync(f.Host);

        Assert.Equal(["skipped several_open_deals"], f.Meta.Sends());
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM pipeline.deals WHERE stage_id = @s", ("s", target)));
        Assert.Equal(1L, await AuditAsync(f, "automations.run.crm_action_skipped"));
    }
}
