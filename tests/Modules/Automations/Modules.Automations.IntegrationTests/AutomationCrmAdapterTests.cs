using BuildingBlocks.Application;
using BuildingBlocks.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Automations.Application;
using Modules.Automations.Domain.Flow;
using Modules.Customers.Application;
using Modules.Customers.Domain;
using Modules.Messaging.Application;
using Modules.Pipeline.Application;
using static Modules.Automations.IntegrationTests.AutomationsApiHarness;

namespace Modules.Automations.IntegrationTests;

/// <summary>
/// Chatbot CRM action spec (2026-11-19) «Which customer»: the Bootstrapper adapter behind <see cref="IAutomationCrm"/>, against
/// PostgreSQL. It resolves the conversation's customer (creating and linking one for the tag operations, like the «Política de datos»
/// block), never creates one to move a deal, and maps what Customers and Pipeline answered to a <see cref="CrmResultCodes"/> code.
/// Conversations are seeded by SQL, as in <see cref="AutomationDataConsentsAdapterTests"/>.
/// </summary>
public sealed class AutomationCrmAdapterTests
{
    private const string UserId = "CO.laura1";
    private const string WaId = "573001234567";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Fixture(
        QepApiFactory Factory,
        WebApplicationFactory<Program> Host,
        string ConnectionString,
        Guid TenantId,
        Guid ConversationId,
        AutomationDataConsentsAdapterTests.EnsureFailure EnsureFailure,
        CrmOverrides Overrides)
    {
        public CrmActionContext Context => new(TenantId, ConversationId);
    }

    /// <summary>What the test asks the Customers and Pipeline entry points to answer (or throw); <c>null</c> = the real service.</summary>
    internal sealed class CrmOverrides
    {
        public Func<ChatbotTagOutcome>? Tags { get; set; }

        public Func<ChatbotDealMoveOutcome>? Deals { get; set; }

        public List<Guid?> DealCustomers { get; } = [];
    }

    private static async Task<Fixture> ArrangeAsync(TestDatabase database, string? userId = UserId)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var failure = new AutomationDataConsentsAdapterTests.EnsureFailure();
        var overrides = new CrmOverrides();
        var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IMessagingNotifier>();
            services.AddSingleton<IMessagingNotifier, SilentNotifier>();
            var directory = services.Single(descriptor => descriptor.ServiceType == typeof(IMessagingCustomerDirectory));
            services.Remove(directory);
            services.AddScoped(provider => AutomationDataConsentsAdapterTests.FailingEnsureDirectory.Wrap(
                (IMessagingCustomerDirectory)ActivatorUtilities.CreateInstance(provider, directory.ImplementationType!), failure));
            services.RemoveAll<IChatbotCustomerTags>();
            services.AddScoped<IChatbotCustomerTags>(provider =>
                new OverridableTags(ActivatorUtilities.CreateInstance<ChatbotCustomerTags>(provider), overrides));
            services.RemoveAll<IChatbotDealMover>();
            services.AddScoped<IChatbotDealMover>(provider =>
                new OverridableDeals(ActivatorUtilities.CreateInstance<ChatbotDealMover>(provider), overrides));
        }));
        var tenant = await RegisterTenantAsync(host);
        await EnableAutomationsAsync(connectionString, tenant.TenantId);
        var connectionId = await SeedWhatsAppConnectionAsync(host, tenant.TenantId, "Ventas", "111", "222");
        var conversationId = await SeedConversationAsync(connectionString, tenant.TenantId, connectionId, userId);
        return new Fixture(factory, host, connectionString, tenant.TenantId, conversationId, failure, overrides);
    }

    private static async Task<Guid> SeedConversationAsync(string connectionString, Guid tenantId, Guid connectionId, string? userId)
    {
        var id = Guid.CreateVersion7();
        await ExecuteAsync(connectionString,
            """
            INSERT INTO messaging.conversations (id, tenant_id, connection_id, user_id, wa_id, profile_name, status, unread_count,
                last_inbound_at, last_activity_at, created_at, updated_at, version)
            VALUES (@id, @t, @c, @u, @wa, 'Laura Gómez', 'Open', 1, now() - interval '5 minutes', now(), now(), now(), 1)
            """,
            ("id", id), ("t", tenantId), ("c", connectionId), ("u", (object?)userId ?? DBNull.Value), ("wa", WaId));
        return id;
    }

    private static async Task<Guid> SeedTagAsync(Fixture f, string name = "Interesado")
    {
        using var scope = f.Host.Services.CreateScope();
        var tag = Tag.Create(TagId.New(), f.TenantId, name, TagColor.Blue, DateTimeOffset.UtcNow);
        scope.ServiceProvider.GetRequiredService<ICustomerTagRepository>().Add(tag);
        await scope.ServiceProvider.GetRequiredService<ICustomersUnitOfWork>().SaveChangesAsync(Ct);
        return tag.Id.Value;
    }

    private static Task<int> EnablePipelineAsync(Fixture f) =>
        ExecuteAsync(f.ConnectionString,
            """
            INSERT INTO tenancy.tenant_modules (tenant_id, module_key, enabled_at, source)
            VALUES (@tenantId, 'pipeline', now(), 'manual')
            ON CONFLICT (tenant_id, module_key) DO UPDATE SET status = 'active', status_changed_at = now()
            """,
            ("tenantId", f.TenantId));

    private static async Task<string> WithAdapterAsync(Fixture f, Func<IAutomationCrm, Task<string>> action)
    {
        using var scope = f.Host.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<IAutomationCrm>());
    }

    private static Task<string> AddTagAsync(Fixture f, Guid tagId) => WithAdapterAsync(f, crm => crm.AddTagAsync(f.Context, tagId, Ct));

    private static Task<string> RemoveTagAsync(Fixture f, Guid tagId) => WithAdapterAsync(f, crm => crm.RemoveTagAsync(f.Context, tagId, Ct));

    private static Task<string> MoveDealAsync(Fixture f, Guid stageId) => WithAdapterAsync(f, crm => crm.MoveDealAsync(f.Context, stageId, Ct));

    private static async Task<Guid?> ConversationCustomerAsync(Fixture f)
    {
        var value = await ScalarAsync<object>(f.ConnectionString,
            "SELECT customer_id FROM messaging.conversations WHERE id = @id", ("id", f.ConversationId));
        return value is Guid id ? id : null;
    }

    private static Task<long> CustomersAsync(Fixture f) =>
        CountAsync(f.ConnectionString, "SELECT count(*) FROM customers.customers WHERE tenant_id = @t", ("t", f.TenantId));

    /// <summary>«Which customer»: without a linked customer the tag operation creates (or finds) one by the BSUID and links it, as
    /// <c>dataConsent</c> does; the tag lands on it.</summary>
    [Fact]
    public async Task AddTagCreatesAndLinksTheCustomerWhenMissing()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var tagId = await SeedTagAsync(f);

        Assert.Equal(CrmResultCodes.Added, await AddTagAsync(f, tagId));

        var customerId = await ConversationCustomerAsync(f);
        Assert.NotNull(customerId);
        Assert.Equal(1L, await CountAsync(f.ConnectionString,
            "SELECT count(*) FROM customers.customer_tags WHERE customer_id = @c AND tag_id = @tag", ("c", customerId.Value), ("tag", tagId)));
        Assert.Equal(CrmResultCodes.AlreadyTagged, await AddTagAsync(f, tagId));
        Assert.Equal(CrmResultCodes.Removed, await RemoveTagAsync(f, tagId));
        Assert.Equal(CrmResultCodes.NotTagged, await RemoveTagAsync(f, tagId));
        Assert.Equal(1L, await CustomersAsync(f));
    }

    /// <summary>A conversation without a channel identity has nobody to tag: skipped, and nobody is created.</summary>
    [Fact]
    public async Task NoUserIsNoCustomer()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database, userId: null);
        using var _ = f.Factory;
        var tagId = await SeedTagAsync(f);

        Assert.Equal(CrmResultCodes.NoCustomer, await AddTagAsync(f, tagId));
        Assert.Equal(CrmResultCodes.NoCustomer, await RemoveTagAsync(f, tagId));

        Assert.Null(await ConversationCustomerAsync(f));
        Assert.Equal(0L, await CustomersAsync(f));
    }

    /// <summary>Unlike <c>dataConsent</c>, a failed creation is not «no customer»: it is an error the flow can tell apart.</summary>
    [Fact]
    public async Task UnavailableCustomerIsCustomerUnavailable()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var tagId = await SeedTagAsync(f);
        f.EnsureFailure.Next = () => new InvalidOperationException("The database rejected the customer.");

        Assert.Equal(CrmResultCodes.CustomerUnavailable, await AddTagAsync(f, tagId));

        Assert.Null(await ConversationCustomerAsync(f));
        Assert.Equal(0L, await CustomersAsync(f));
    }

    [Fact]
    public async Task TagMappingCoversEveryOutcome()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        (ChatbotTagOutcome Outcome, string Code)[] table =
        [
            (ChatbotTagOutcome.Added, CrmResultCodes.Added),
            (ChatbotTagOutcome.AlreadyTagged, CrmResultCodes.AlreadyTagged),
            (ChatbotTagOutcome.Removed, CrmResultCodes.Removed),
            (ChatbotTagOutcome.NotTagged, CrmResultCodes.NotTagged),
            (ChatbotTagOutcome.TagMissing, CrmResultCodes.TagMissing),
            (ChatbotTagOutcome.LimitReached, CrmResultCodes.TagLimit),
            (ChatbotTagOutcome.CustomerMissing, CrmResultCodes.CustomerUnavailable),
        ];
        Assert.Equal(Enum.GetValues<ChatbotTagOutcome>().Length, table.Length);

        foreach (var (outcome, code) in table)
        {
            f.Overrides.Tags = () => outcome;
            Assert.Equal(code, await AddTagAsync(f, Guid.CreateVersion7()));
            Assert.Equal(code, await RemoveTagAsync(f, Guid.CreateVersion7()));
        }
    }

    [Fact]
    public async Task DealMappingCoversEveryOutcomeAndPassesTheLinkedCustomer()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await EnablePipelineAsync(f);
        (ChatbotDealMoveOutcome Outcome, string Code)[] table =
        [
            (ChatbotDealMoveOutcome.Moved, CrmResultCodes.Moved),
            (ChatbotDealMoveOutcome.AlreadyInStage, CrmResultCodes.AlreadyInStage),
            (ChatbotDealMoveOutcome.NoOpenDeal, CrmResultCodes.NoOpenDeal),
            (ChatbotDealMoveOutcome.SeveralOpenDeals, CrmResultCodes.SeveralOpenDeals),
            (ChatbotDealMoveOutcome.StageNotOpen, CrmResultCodes.StageNotOpen),
            (ChatbotDealMoveOutcome.StageMissing, CrmResultCodes.StageMissing),
            (ChatbotDealMoveOutcome.ModuleOff, CrmResultCodes.ModuleOff),
            (ChatbotDealMoveOutcome.Conflict, CrmResultCodes.DealConflict),
        ];
        Assert.Equal(Enum.GetValues<ChatbotDealMoveOutcome>().Length, table.Length);

        foreach (var (outcome, code) in table)
        {
            f.Overrides.Deals = () => outcome;
            Assert.Equal(code, await MoveDealAsync(f, Guid.CreateVersion7()));
        }

        // The conversation's customer as it is: none here, so null every time.
        Assert.All(f.Overrides.DealCustomers, Assert.Null);
        var customerId = Guid.CreateVersion7();
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.conversations SET customer_id = @c WHERE id = @id", ("c", customerId), ("id", f.ConversationId));
        await MoveDealAsync(f, Guid.CreateVersion7());
        Assert.Equal(customerId, f.Overrides.DealCustomers[^1]);
    }

    /// <summary>A rule broken inside Customers is the block's <c>error</c> exit with <c>failed</c>, never an exception to the engine.</summary>
    [Fact]
    public async Task ADomainErrorIsFailed()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        f.Overrides.Tags = () => throw new TestDomainException();

        Assert.Equal(CrmResultCodes.Failed, await AddTagAsync(f, Guid.CreateVersion7()));
    }

    /// <summary>A concurrency conflict from Customers is «not yet», not «could not»: it goes up and the engine unit is retried.</summary>
    [Fact]
    public async Task AConcurrencyConflictFromTheTagsPropagates()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        f.Overrides.Tags = () => throw new RequestConcurrencyException("concurrency.conflict", "Deadlock detected.");

        await Assert.ThrowsAsync<RequestConcurrencyException>(() => AddTagAsync(f, Guid.CreateVersion7()));
    }

    /// <summary>«Which customer»: moving a deal never creates a customer; without one and without a deal from this conversation it is
    /// skipped.</summary>
    [Fact]
    public async Task MoveDealNeverCreatesACustomer()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await EnablePipelineAsync(f);
        Guid stageId;
        using (var scope = f.Host.Services.CreateScope())
        {
            var pipeline = await scope.ServiceProvider.GetRequiredService<IPipelineProvisioner>().EnsureAsync(f.TenantId, Ct);
            stageId = pipeline.FirstOpenStage.Id;
        }

        Assert.Equal(CrmResultCodes.NoOpenDeal, await MoveDealAsync(f, stageId));

        Assert.Null(await ConversationCustomerAsync(f));
        Assert.Equal(0L, await CustomersAsync(f));
    }

    [Fact]
    public async Task AModuleOffIsModuleOff()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var tagId = await SeedTagAsync(f);
        await DisableModuleAsync(f.ConnectionString, f.TenantId, "customers");

        Assert.Equal(CrmResultCodes.ModuleOff, await AddTagAsync(f, tagId));
        Assert.Equal(CrmResultCodes.ModuleOff, await RemoveTagAsync(f, tagId));
        // Pipeline does not come with the signup.
        Assert.Equal(CrmResultCodes.ModuleOff, await MoveDealAsync(f, Guid.CreateVersion7()));

        Assert.Null(await ConversationCustomerAsync(f));
        Assert.Equal(0L, await CustomersAsync(f));
    }

    [Fact]
    public async Task AnUnknownConversationIsFailed()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var other = new CrmActionContext(f.TenantId, Guid.CreateVersion7());

        Assert.Equal(CrmResultCodes.Failed, await WithAdapterAsync(f, crm => crm.AddTagAsync(other, Guid.CreateVersion7(), Ct)));
        Assert.Equal(CrmResultCodes.Failed, await WithAdapterAsync(f, crm => crm.MoveDealAsync(other, Guid.CreateVersion7(), Ct)));
    }

    private sealed class TestDomainException() : DomainException("customers.test", "A rule was broken.");

    private sealed class OverridableTags(IChatbotCustomerTags inner, CrmOverrides overrides) : IChatbotCustomerTags
    {
        public Task<ChatbotTagOutcome> AddAsync(Guid tenantId, Guid customerId, Guid tagId, CancellationToken cancellationToken) =>
            overrides.Tags is { } answer ? Task.FromResult(answer()) : inner.AddAsync(tenantId, customerId, tagId, cancellationToken);

        public Task<ChatbotTagOutcome> RemoveAsync(Guid tenantId, Guid customerId, Guid tagId, CancellationToken cancellationToken) =>
            overrides.Tags is { } answer ? Task.FromResult(answer()) : inner.RemoveAsync(tenantId, customerId, tagId, cancellationToken);
    }

    private sealed class OverridableDeals(IChatbotDealMover inner, CrmOverrides overrides) : IChatbotDealMover
    {
        public Task<ChatbotDealMoveOutcome> MoveAsync(
            Guid tenantId, Guid? customerId, Guid conversationId, Guid stageId, CancellationToken cancellationToken)
        {
            overrides.DealCustomers.Add(customerId);
            return overrides.Deals is { } answer
                ? Task.FromResult(answer())
                : inner.MoveAsync(tenantId, customerId, conversationId, stageId, cancellationToken);
        }
    }

    private sealed class SilentNotifier : IMessagingNotifier
    {
        public Task ConversationChangedAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task InboundMessageAsync(
            Guid tenantId, Guid conversationId, Guid messageId, Guid? assignedMemberId, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
