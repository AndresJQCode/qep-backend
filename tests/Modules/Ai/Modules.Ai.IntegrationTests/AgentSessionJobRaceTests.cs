using Microsoft.Extensions.DependencyInjection;
using Modules.Ai.Application;
using static Modules.Ai.IntegrationTests.AiApiHarness;

namespace Modules.Ai.IntegrationTests;

/// <summary>
/// Revisión final, hallazgo 3: el procesador de entrantes sube <c>up_to_seq</c> del trabajo pendiente de la sesión (D-A14) mientras el worker
/// puede estar reclamándolo sin el candado de la conversación. La lectura del pendiente toma la fila (<c>FOR UPDATE</c>): el reclamo la salta
/// hasta que la unidad confirma, y el trabajo sale con los turnos nuevos.
/// </summary>
public sealed class AgentSessionJobRaceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheWorkerCannotClaimAPendingJobWhileTheInboxExtendsIt()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString, settings: new Dictionary<string, string> { ["Ai:Workers:Enabled"] = "false" });
        await SeedTenantAsync(factory);
        var sessionId = Guid.CreateVersion7();
        var jobId = Guid.CreateVersion7();
        var due = DateTimeOffset.UtcNow.AddMinutes(-1);
        await ExecuteAsync(
            connectionString,
            """
            INSERT INTO ai.model_jobs (id, tenant_id, session_id, up_to_seq, status, attempts, next_attempt_at, created_at)
            VALUES (@id, @tenant, @session, 1, 'Pending', 0, @due, @due)
            """,
            ("id", jobId), ("tenant", Guid.Parse(TenantId)), ("session", sessionId), ("due", due));

        await using (var inboxScope = factory.Services.CreateAsyncScope())
        {
            var unitOfWork = inboxScope.ServiceProvider.GetRequiredService<IAiSessionUnitOfWork>();
            await using var transaction = await unitOfWork.BeginTransactionAsync(Ct);
            var pending = await inboxScope.ServiceProvider.GetRequiredService<IAgentSessionRepository>().FindPendingJobAsync(sessionId, Ct);
            Assert.NotNull(pending);

            // El worker llega mientras la unidad tiene la fila: no la reclama.
            await using (var workerScope = factory.Services.CreateAsyncScope())
            {
                Assert.Null(await workerScope.ServiceProvider.GetRequiredService<IAiModelJobs>().ClaimAsync(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(3), Ct));
            }

            pending.ExtendTo(2);
            await unitOfWork.SaveChangesAsync(Ct);
        }

        await using var afterScope = factory.Services.CreateAsyncScope();
        var claimed = await afterScope.ServiceProvider.GetRequiredService<IAiModelJobs>().ClaimAsync(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(3), Ct);
        Assert.NotNull(claimed);
        Assert.Equal((jobId, 2), (claimed.Id, claimed.UpToSeq));
    }

    /// <summary>
    /// Spec 2026-11-17 §6.6: el agente responde justo cuando vence la inactividad del bloque. El barrido corta por los minutos de la sesión
    /// (no por las 24 h de siempre), la cierra con el candado de la conversación, y el trabajo que llega después encuentra la sesión terminada
    /// y se descarta sin enviar nada. Una sesión de conexión con el mismo silencio sigue viva.
    /// </summary>
    [Fact]
    public async Task TheAgentReplyingWhenTheIdleCutFiresIsDiscarded()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString, settings: new Dictionary<string, string> { ["Ai:Workers:Enabled"] = "false" });
        await SeedTenantAsync(factory);
        var tenant = Guid.Parse(TenantId);
        var block = Guid.CreateVersion7();
        var connection = Guid.CreateVersion7();
        var jobId = Guid.CreateVersion7();
        var quiet = DateTimeOffset.UtcNow.AddMinutes(-10);
        await ExecuteAsync(
            connectionString,
            """
            INSERT INTO ai.sessions (id, tenant_id, conversation_id, connection_id, agent_id, agent_version_id, status, started_at, updated_at,
                                     origin, delegation_id, chatbot_id, run_id, idle_minutes)
            VALUES (@block, @tenant, gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), 'Live', @quiet, @quiet,
                    'ChatbotBlock', gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), 5),
                   (@connection, @tenant, gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), 'Live', @quiet, @quiet,
                    'Connection', NULL, NULL, NULL, NULL);
            INSERT INTO ai.model_jobs (id, tenant_id, session_id, up_to_seq, status, attempts, next_attempt_at, created_at, result)
            VALUES (@job, @tenant, @block, 1, 'Pending', 0, @quiet, @quiet, '{"type":"replied","text":"Hola."}'::jsonb);
            """,
            ("block", block), ("connection", connection), ("tenant", tenant), ("quiet", quiet), ("job", jobId));

        await using (var sweepScope = factory.Services.CreateAsyncScope())
        {
            Assert.Equal(1, await sweepScope.ServiceProvider.GetRequiredService<AiSessionSweeper>().SweepIdleAsync(Ct));
        }

        await using (var workerScope = factory.Services.CreateAsyncScope())
        {
            var job = await workerScope.ServiceProvider.GetRequiredService<IAiModelJobs>().ClaimAsync(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(3), Ct);
            Assert.NotNull(job);
            await workerScope.ServiceProvider.GetRequiredService<AgentSessionJobProcessor>().ProcessAsync(job, Ct);
        }

        // Sin conversación en Mensajería no hay a quién devolver: la sesión del bloque queda Interrupted, nunca viva.
        Assert.Equal("Interrupted", await ScalarAsync<string>(connectionString, "SELECT status FROM ai.sessions WHERE id = @id", ("id", block)));
        Assert.Equal("Live", await ScalarAsync<string>(connectionString, "SELECT status FROM ai.sessions WHERE id = @id", ("id", connection)));
        Assert.Equal(
            ("Discarded", "session_ended"),
            (await ScalarAsync<string>(connectionString, "SELECT status FROM ai.model_jobs WHERE id = @id", ("id", jobId)),
             await ScalarAsync<string>(connectionString, "SELECT outcome FROM ai.model_jobs WHERE id = @id", ("id", jobId))));
    }

    /// <summary>Spec 2026-11-17 §6.5: una delegación viva que ninguna conversación apunta pasa a Interrupted; una devuelta no se toca.</summary>
    [Fact]
    public async Task AnOrphanDelegationIsInterruptedByTheSweep()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString, settings: new Dictionary<string, string> { ["Ai:Workers:Enabled"] = "false" });
        await SeedTenantAsync(factory);
        var (orphan, returned) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        await ExecuteAsync(
            connectionString,
            """
            INSERT INTO messaging.bot_delegations (id, tenant_id, conversation_id, connection_id, chatbot_id, run_id, node_id, agent_id, brief,
                status, result, started_at, ended_at)
            VALUES (@orphan, @tenant, gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), 'ai-agent-1', gen_random_uuid(),
                    '{"objective":"x"}', 'Live', NULL, now(), NULL),
                   (@returned, @tenant, gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), 'ai-agent-1', gen_random_uuid(),
                    '{"objective":"x"}', 'Returned', '{"output":"done","reason":"objective_completed"}', now(), now());
            """,
            ("orphan", orphan), ("returned", returned), ("tenant", Guid.Parse(TenantId)));

        await using var scope = factory.Services.CreateAsyncScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<AiSessionSweeper>().SweepOrphanDelegationsAsync(Ct));

        Assert.Equal("Interrupted", await ScalarAsync<string>(connectionString, "SELECT status FROM messaging.bot_delegations WHERE id = @id", ("id", orphan)));
        Assert.Equal("Returned", await ScalarAsync<string>(connectionString, "SELECT status FROM messaging.bot_delegations WHERE id = @id", ("id", returned)));
    }
}
