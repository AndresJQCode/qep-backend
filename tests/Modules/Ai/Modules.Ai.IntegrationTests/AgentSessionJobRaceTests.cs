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
}
