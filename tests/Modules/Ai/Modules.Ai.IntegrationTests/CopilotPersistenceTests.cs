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

    // Plan fase 2, Parte C (decisión del plan): la lista de agentes no suma el consumo del copiloto, que no tiene agente.
    [Fact]
    public async Task TheAgentsTotalExcludesTheCopilot()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = Factory(connectionString);
        await SeedTenantAsync(factory);
        var agent = Guid.CreateVersion7();
        await InsertUsageAsync(connectionString, agentId: agent, threadId: null, memberId: null);
        await InsertUsageAsync(connectionString, agentId: null, threadId: Guid.CreateVersion7(), memberId: Member);

        await using var scope = factory.Services.CreateAsyncScope();
        var month = DateTimeOffset.UtcNow.AddDays(-1);
        var activity = await scope.ServiceProvider.GetRequiredService<IAgentActivityReader>()
            .SummarizeAsync(Tenant, new AgentActivityWindow(month, month, DateTimeOffset.UtcNow.AddDays(1)), Ct);

        var usage = Assert.Single(Assert.Single(activity.UsageByAgent).Value);
        Assert.Equal((agent, 1, 10L, 5L), (activity.UsageByAgent.Keys.Single(), usage.Calls, usage.InputTokens, usage.OutputTokens));
    }

    // D-C12: el mes del copiloto por miembro; el consumo de un agente no entra.
    [Fact]
    public async Task TheCopilotUsageIsSummedPerMember()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = Factory(connectionString);
        await SeedTenantAsync(factory);
        var other = Guid.CreateVersion7();
        await InsertUsageAsync(connectionString, agentId: null, threadId: Guid.CreateVersion7(), memberId: Member);
        await InsertUsageAsync(connectionString, agentId: null, threadId: Guid.CreateVersion7(), memberId: Member);
        await InsertUsageAsync(connectionString, agentId: null, threadId: Guid.CreateVersion7(), memberId: other);
        await InsertUsageAsync(connectionString, agentId: Guid.CreateVersion7(), threadId: null, memberId: null);

        await using var scope = factory.Services.CreateAsyncScope();
        var rows = await scope.ServiceProvider.GetRequiredService<ICopilotRepository>()
            .SumTokensByMemberAsync(Tenant, DateTimeOffset.UtcNow.AddDays(-1), Ct);

        Assert.Equal(
            [new CopilotMemberTokenUsage(Member, 20, 10), new CopilotMemberTokenUsage(other, 10, 5)],
            rows.OrderByDescending(row => row.InputTokens));
    }

    // D-C8 y D-C9: el barrido borra el hilo vencido con la retención del tenant (y lo audita con actor sistema), deja el hilo vivo, y borra
    // sólo los tool_calls del copiloto con más de 30 días.
    [Fact]
    public async Task TheRetentionSweepDeletesExpiredThreadsAndOldCopilotToolCalls()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = Factory(connectionString);
        await SeedTenantAsync(factory);
        var now = DateTimeOffset.UtcNow;
        var expired = CopilotThread.Create(Guid.CreateVersion7(), Tenant, Member, "¿Cuánto vendimos?", Guid.CreateVersion7(), now.AddDays(-91));
        var alive = CopilotThread.Create(Guid.CreateVersion7(), Tenant, Member, "¿Y hoy?", Guid.CreateVersion7(), now.AddDays(-89));
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<ICopilotRepository>();
            repository.AddThread(expired);
            repository.AddThread(alive);
            await scope.ServiceProvider.GetRequiredService<IAiUnitOfWork>().SaveChangesAsync(Ct);
        }

        var aliveJob = Guid.CreateVersion7();
        var agentJob = Guid.CreateVersion7();
        await InsertJobWithIdAsync(connectionString, aliveJob, sessionId: null, testId: null, threadId: alive.Id);
        await InsertJobWithIdAsync(connectionString, agentJob, sessionId: null, testId: Guid.CreateVersion7(), threadId: null);
        await InsertToolCallAsync(connectionString, aliveJob, "old", now.AddDays(-31));
        await InsertToolCallAsync(connectionString, aliveJob, "new", now.AddDays(-1));
        await InsertToolCallAsync(connectionString, agentJob, "agent", now.AddDays(-60));

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var swept = await scope.ServiceProvider.GetRequiredService<CopilotRetentionSweeper>().SweepAsync(Ct);
            Assert.Equal((1, 1), (swept.Threads, swept.ToolCalls));
        }

        Assert.Equal(1L, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM ai.copilot_threads"));
        Assert.Equal(
            ["agent", "new"],
            (await ScalarAsync<string>(connectionString, "SELECT string_agg(call_id, ',' ORDER BY call_id) FROM ai.tool_calls"))!.Split(','));
        Assert.Equal(
            1L,
            await ScalarAsync<long>(
                connectionString,
                "SELECT count(*) FROM audit.entries WHERE action = 'ai.copilot.thread_deleted' AND actor_type = 'System' AND resource_id = @id",
                ("id", expired.Id.ToString())));
    }

    private static Task<int> InsertJobWithIdAsync(string connectionString, Guid id, Guid? sessionId, Guid? testId, Guid? threadId) =>
        ExecuteAsync(
            connectionString,
            """
            INSERT INTO ai.model_jobs (id, tenant_id, session_id, test_id, copilot_thread_id, up_to_seq, status, attempts, next_attempt_at, created_at)
            VALUES (@id, @tenant, @session, @test, @thread, 1, 'Done', 1, now(), now());
            """,
            ("id", id), ("tenant", Tenant), ("session", (object?)sessionId ?? DBNull.Value),
            ("test", (object?)testId ?? DBNull.Value), ("thread", (object?)threadId ?? DBNull.Value));

    private static Task<int> InsertToolCallAsync(string connectionString, Guid jobId, string callId, DateTimeOffset createdAt) =>
        ExecuteAsync(
            connectionString,
            """
            INSERT INTO ai.tool_calls (tenant_id, job_id, call_id, tool, arguments, result, outcome, duration_ms, created_at)
            VALUES (@tenant, @job, @call, 'get_orders_summary', '{}'::jsonb, '{}'::jsonb, 'ok', 5, @at);
            """,
            ("tenant", Tenant), ("job", jobId), ("call", callId), ("at", createdAt));

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
