using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Modules.Customers.Application;
using Modules.Customers.Domain;
using Npgsql;
using static Modules.Customers.IntegrationTests.CustomersApiHarness;

namespace Modules.Customers.IntegrationTests;

/// <summary>
/// Chatbot CRM action spec (2026-11-19) against the database: <see cref="IChatbotCustomerTags"/> adds or removes one tag as the
/// system (no member, no permission), idempotently, and audits only a real change, with source <c>chatbot</c>. The customer is
/// seeded the way the bot's ingestion creates it (<see cref="ICustomerWhatsAppDirectory.EnsureAsync"/>): an Incomplete customer.
/// </summary>
public sealed class ChatbotCustomerTagsTests
{
    private static readonly Guid Tenant = Guid.Parse(TenantId);
    private static readonly Guid OtherTenant = Guid.Parse(OtherTenantId);

    private sealed class Fixture(TestDatabase database, QepApiFactory factory) : IAsyncDisposable
    {
        public string ConnectionString { get; } = database.GetConnectionString();

        public static async Task<Fixture> StartAsync()
        {
            var database = await StartDatabaseAsync();
            var factory = new QepApiFactory(database.GetConnectionString());
            await SeedTenantAsync(factory);
            return new Fixture(database, factory);
        }

        public async Task<Guid> SeedIncompleteCustomerAsync()
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var directory = scope.ServiceProvider.GetRequiredService<ICustomerWhatsAppDirectory>();
            var ensured = await directory.EnsureAsync(
                Tenant, new WhatsAppContact($"CO.{Guid.NewGuid():N}", null, "Laura", null), TestContext.Current.CancellationToken);
            return ensured.CustomerId;
        }

        public async Task<Guid> SeedTagAsync(Guid tenantId, string name)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var tag = Tag.Create(TagId.New(), tenantId, name, TagColor.Blue, DateTimeOffset.UtcNow);
            scope.ServiceProvider.GetRequiredService<ICustomerTagRepository>().Add(tag);
            await scope.ServiceProvider.GetRequiredService<ICustomersUnitOfWork>().SaveChangesAsync(TestContext.Current.CancellationToken);
            return tag.Id.Value;
        }

        public async Task AssignAsync(Guid customerId, IEnumerable<Guid> tagIds)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var tags = scope.ServiceProvider.GetRequiredService<ICustomerTagRepository>();
            foreach (var tagId in tagIds)
            {
                tags.AddAssignment(CustomerTagAssignment.Create(
                    new CustomerId(customerId), new TagId(tagId), Tenant, Guid.CreateVersion7(), DateTimeOffset.UtcNow));
            }

            await scope.ServiceProvider.GetRequiredService<ICustomersUnitOfWork>().SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        public async Task<ChatbotTagOutcome> AddAsync(Guid customerId, Guid tagId)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IChatbotCustomerTags>()
                .AddAsync(Tenant, customerId, tagId, TestContext.Current.CancellationToken);
        }

        public async Task<ChatbotTagOutcome> RemoveAsync(Guid customerId, Guid tagId)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IChatbotCustomerTags>()
                .RemoveAsync(Tenant, customerId, tagId, TestContext.Current.CancellationToken);
        }

        public Task MarkMergedAsync(Guid mergedId, Guid survivorId) =>
            CustomersApiHarness.MarkMergedAsync(ConnectionString, mergedId, survivorId);

        public async ValueTask DisposeAsync()
        {
            await factory.DisposeAsync();
            await database.DisposeAsync();
        }
    }

    private static async Task<string?> TextAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static async Task<long> CountAsync(string connectionString, string sql) =>
        long.Parse((await TextAsync(connectionString, sql))!, CultureInfo.InvariantCulture);

    private static Task<long> AssignmentCountAsync(string connectionString, Guid customerId, Guid tagId) =>
        CountAsync(connectionString, $"SELECT count(*) FROM customers.customer_tags WHERE customer_id = '{customerId}' AND tag_id = '{tagId}'");

    // The chatbot's audit row: system actor, empty id, source chatbot, one-tag change reported as a change of the set.
    private static Task<long> ChatbotAuditCountAsync(string connectionString, Guid customerId) =>
        CountAsync(
            connectionString,
            "SELECT count(*) FROM platform.outbox_messages WHERE event_name = 'platform.audit.recorded.v1' "
            + $"AND payload->>'action' = '{CustomerAuditActions.TagsChanged}' AND payload->>'resourceId' = '{customerId}' "
            + $"AND payload->>'actorType' = 'System' AND payload->>'actorId' = '{Guid.Empty}' AND payload->>'source' = 'chatbot'");

    private static Task<long> AnyTagAuditCountAsync(string connectionString) =>
        CountAsync(
            connectionString,
            $"SELECT count(*) FROM platform.outbox_messages WHERE payload->>'action' = '{CustomerAuditActions.TagsChanged}'");

    [Fact]
    public async Task AddsATagToAnIncompleteCustomer()
    {
        await using var fixture = await Fixture.StartAsync();
        var customerId = await fixture.SeedIncompleteCustomerAsync();
        var tagId = await fixture.SeedTagAsync(Tenant, "Interesado");
        Assert.Equal("Incomplete", await TextAsync(fixture.ConnectionString, $"SELECT completeness FROM customers.customers WHERE id = '{customerId}'"));

        var outcome = await fixture.AddAsync(customerId, tagId);

        Assert.Equal(ChatbotTagOutcome.Added, outcome);
        Assert.Equal(1L, await AssignmentCountAsync(fixture.ConnectionString, customerId, tagId));
        // The plan's assumption: the system assigns with an empty user id, and nothing in the table rejects it.
        Assert.Equal(
            Guid.Empty.ToString(),
            await TextAsync(fixture.ConnectionString, $"SELECT assigned_by_user_id FROM customers.customer_tags WHERE customer_id = '{customerId}'"));
        Assert.Equal(1L, await ChatbotAuditCountAsync(fixture.ConnectionString, customerId));
    }

    [Fact]
    public async Task AddingTwiceIsAlreadyTaggedAndAuditsOnce()
    {
        await using var fixture = await Fixture.StartAsync();
        var customerId = await fixture.SeedIncompleteCustomerAsync();
        var tagId = await fixture.SeedTagAsync(Tenant, "Interesado");

        Assert.Equal(ChatbotTagOutcome.Added, await fixture.AddAsync(customerId, tagId));
        Assert.Equal(ChatbotTagOutcome.AlreadyTagged, await fixture.AddAsync(customerId, tagId));

        Assert.Equal(1L, await AssignmentCountAsync(fixture.ConnectionString, customerId, tagId));
        Assert.Equal(1L, await ChatbotAuditCountAsync(fixture.ConnectionString, customerId));
    }

    [Fact]
    public async Task AtTheLimitReturnsLimitReached()
    {
        await using var fixture = await Fixture.StartAsync();
        var customerId = await fixture.SeedIncompleteCustomerAsync();
        var full = new List<Guid>();
        for (var index = 0; index < CustomerTagAssignment.MaxPerCustomer; index++)
        {
            full.Add(await fixture.SeedTagAsync(Tenant, $"Tag {index}"));
        }

        await fixture.AssignAsync(customerId, full);
        var oneMore = await fixture.SeedTagAsync(Tenant, "One more");

        var outcome = await fixture.AddAsync(customerId, oneMore);

        Assert.Equal(ChatbotTagOutcome.LimitReached, outcome);
        Assert.Equal(0L, await AssignmentCountAsync(fixture.ConnectionString, customerId, oneMore));
        Assert.Equal(
            CustomerTagAssignment.MaxPerCustomer,
            await CountAsync(fixture.ConnectionString, $"SELECT count(*) FROM customers.customer_tags WHERE customer_id = '{customerId}'"));
        Assert.Equal(0L, await AnyTagAuditCountAsync(fixture.ConnectionString));
    }

    [Fact]
    public async Task TagOfAnotherTenantIsTagMissing()
    {
        await using var fixture = await Fixture.StartAsync();
        var customerId = await fixture.SeedIncompleteCustomerAsync();
        var foreignTag = await fixture.SeedTagAsync(OtherTenant, "Interesado");

        Assert.Equal(ChatbotTagOutcome.TagMissing, await fixture.AddAsync(customerId, foreignTag));
        Assert.Equal(ChatbotTagOutcome.TagMissing, await fixture.RemoveAsync(customerId, foreignTag));
        Assert.Equal(ChatbotTagOutcome.TagMissing, await fixture.AddAsync(customerId, Guid.CreateVersion7()));

        Assert.Equal(0L, await CountAsync(fixture.ConnectionString, "SELECT count(*) FROM customers.customer_tags"));
        Assert.Equal(0L, await AnyTagAuditCountAsync(fixture.ConnectionString));
    }

    [Fact]
    public async Task UnknownCustomerIsCustomerMissing()
    {
        await using var fixture = await Fixture.StartAsync();
        var tagId = await fixture.SeedTagAsync(Tenant, "Interesado");

        Assert.Equal(ChatbotTagOutcome.CustomerMissing, await fixture.AddAsync(Guid.CreateVersion7(), tagId));
        Assert.Equal(ChatbotTagOutcome.CustomerMissing, await fixture.RemoveAsync(Guid.CreateVersion7(), tagId));
        Assert.Equal(0L, await AnyTagAuditCountAsync(fixture.ConnectionString));
    }

    [Fact]
    public async Task AMergedCustomerIsTaggedOnTheSurvivor()
    {
        await using var fixture = await Fixture.StartAsync();
        var survivor = await fixture.SeedIncompleteCustomerAsync();
        var merged = await fixture.SeedIncompleteCustomerAsync();
        await fixture.MarkMergedAsync(merged, survivor);
        var tagId = await fixture.SeedTagAsync(Tenant, "Interesado");

        Assert.Equal(ChatbotTagOutcome.Added, await fixture.AddAsync(merged, tagId));

        Assert.Equal(1L, await AssignmentCountAsync(fixture.ConnectionString, survivor, tagId));
        Assert.Equal(0L, await AssignmentCountAsync(fixture.ConnectionString, merged, tagId));
        Assert.Equal(1L, await ChatbotAuditCountAsync(fixture.ConnectionString, survivor));
    }

    [Fact]
    public async Task RemoveAbsentIsNotTagged()
    {
        await using var fixture = await Fixture.StartAsync();
        var customerId = await fixture.SeedIncompleteCustomerAsync();
        var tagId = await fixture.SeedTagAsync(Tenant, "Interesado");

        Assert.Equal(ChatbotTagOutcome.NotTagged, await fixture.RemoveAsync(customerId, tagId));
        Assert.Equal(0L, await AnyTagAuditCountAsync(fixture.ConnectionString));
    }

    [Fact]
    public async Task RemoveRemovesAndAudits()
    {
        await using var fixture = await Fixture.StartAsync();
        var customerId = await fixture.SeedIncompleteCustomerAsync();
        var kept = await fixture.SeedTagAsync(Tenant, "Cliente");
        var tagId = await fixture.SeedTagAsync(Tenant, "Interesado");
        await fixture.AssignAsync(customerId, [kept, tagId]);

        var outcome = await fixture.RemoveAsync(customerId, tagId);

        Assert.Equal(ChatbotTagOutcome.Removed, outcome);
        Assert.Equal(0L, await AssignmentCountAsync(fixture.ConnectionString, customerId, tagId));
        Assert.Equal(1L, await AssignmentCountAsync(fixture.ConnectionString, customerId, kept));
        Assert.Equal(1L, await ChatbotAuditCountAsync(fixture.ConnectionString, customerId));
    }
}
