using Microsoft.Extensions.DependencyInjection;
using Modules.Ai.Application;
using Modules.Ai.Domain;
using Npgsql;
using static Modules.Ai.IntegrationTests.AiApiHarness;

namespace Modules.Ai.IntegrationTests;

/// <summary>
/// Spec 2026-11-17 (copiloto) §5.1 y migración <c>AddCopilot</c>: los <c>CHECK</c> nuevos de <c>model_jobs</c> y <c>usage_records</c>, y el
/// hilo con sus mensajes ida y vuelta por el repositorio.
/// </summary>
public sealed class CopilotPersistenceTests
{
    private static readonly Guid Tenant = Guid.Parse(TenantId);
    private static readonly Guid Member = Guid.Parse(SubjectId);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AJobHasExactlyOneOwner()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = Factory(connectionString);
        await SeedTenantAsync(factory);

        // Uno solo: el del copiloto entra.
        Assert.Equal(1, await InsertJobAsync(connectionString, sessionId: null, testId: null, threadId: Guid.CreateVersion7()));

        // Ninguno, o dos a la vez: el CHECK de tres dueños lo rechaza.
        await AssertCheckAsync("CK_model_jobs_owner", () => InsertJobAsync(connectionString, null, null, null));
        await AssertCheckAsync("CK_model_jobs_owner", () => InsertJobAsync(connectionString, null, Guid.CreateVersion7(), Guid.CreateVersion7()));
        await AssertCheckAsync(
            "CK_model_jobs_owner", () => InsertJobAsync(connectionString, Guid.CreateVersion7(), Guid.CreateVersion7(), null));
    }

    [Fact]
    public async Task ACopilotUsageRecordRequiresTheMember()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = Factory(connectionString);
        await SeedTenantAsync(factory);
        var thread = Guid.CreateVersion7();

        Assert.Equal(1, await InsertUsageAsync(connectionString, agentId: null, threadId: thread, memberId: Member));
        Assert.Equal(1, await InsertUsageAsync(connectionString, agentId: Guid.CreateVersion7(), threadId: null, memberId: null));

        await AssertCheckAsync("CK_usage_records_copilot_member", () => InsertUsageAsync(connectionString, null, thread, null));
        await AssertCheckAsync("CK_usage_records_owner", () => InsertUsageAsync(connectionString, null, null, Member));
        await AssertCheckAsync("CK_usage_records_owner", () => InsertUsageAsync(connectionString, Guid.CreateVersion7(), thread, Member));
    }

    [Fact]
    public async Task AThreadRoundTripsWithItsMessagesAndOnlyForItsOwner()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = Factory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        var jobId = Guid.CreateVersion7();
        var thread = CopilotThread.Create(Guid.CreateVersion7(), Tenant, Member, "¿Cuánto vendimos?", jobId, DateTimeOffset.UtcNow);
        thread.CompleteAssistant(jobId, "Doce pedidos.", [new CopilotCitation("order", "o-1", "Pedido 1")], DateTimeOffset.UtcNow);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ICopilotRepository>().AddThread(thread);
            await scope.ServiceProvider.GetRequiredService<IAiUnitOfWork>().SaveChangesAsync(Ct);
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<ICopilotRepository>();
            var loaded = await repository.FindThreadAsync(Tenant, Member, thread.Id, Ct);
            Assert.NotNull(loaded);
            Assert.Equal([1, 2], loaded.Messages.Select(message => message.Seq));
            Assert.Equal([new CopilotCitation("order", "o-1", "Pedido 1")], loaded.Messages[1].Citations);
            Assert.Null(await repository.FindThreadAsync(Tenant, Guid.CreateVersion7(), thread.Id, Ct));
            Assert.Null(await repository.FindThreadAsync(Guid.Parse(OtherTenantId), Member, thread.Id, Ct));
        }
    }

    private static QepApiFactory Factory(string connectionString) =>
        new(connectionString, settings: new Dictionary<string, string> { ["Ai:Workers:Enabled"] = "false" });

    private static async Task AssertCheckAsync(string constraint, Func<Task<int>> insert)
    {
        var error = await Assert.ThrowsAsync<PostgresException>(insert);
        Assert.Equal((PostgresErrorCodes.CheckViolation, constraint), (error.SqlState, error.ConstraintName));
    }

    private static Task<int> InsertJobAsync(string connectionString, Guid? sessionId, Guid? testId, Guid? threadId) =>
        ExecuteAsync(
            connectionString,
            """
            INSERT INTO ai.model_jobs (id, tenant_id, session_id, test_id, copilot_thread_id, up_to_seq, status, attempts, next_attempt_at, created_at)
            VALUES (@id, @tenant, @session, @test, @thread, 1, 'Pending', 0, now(), now());
            """,
            ("id", Guid.CreateVersion7()), ("tenant", Tenant), ("session", (object?)sessionId ?? DBNull.Value),
            ("test", (object?)testId ?? DBNull.Value), ("thread", (object?)threadId ?? DBNull.Value));

    private static Task<int> InsertUsageAsync(string connectionString, Guid? agentId, Guid? threadId, Guid? memberId) =>
        ExecuteAsync(
            connectionString,
            """
            INSERT INTO ai.usage_records (tenant_id, agent_id, copilot_thread_id, member_user_id, job_id, provider, model, input_tokens,
                                          output_tokens, outcome, called_at)
            VALUES (@tenant, @agent, @thread, @member, @job, 'anthropic', 'claude-opus-5-5', 10, 5, 'ok', now());
            """,
            ("tenant", Tenant), ("agent", (object?)agentId ?? DBNull.Value), ("thread", (object?)threadId ?? DBNull.Value),
            ("member", (object?)memberId ?? DBNull.Value), ("job", Guid.CreateVersion7()));
}
