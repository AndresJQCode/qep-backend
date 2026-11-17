using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Modules.Ai.Application;
using Modules.Ai.Domain;

namespace Modules.Ai.Infrastructure.Persistence;

internal sealed class AgentTestRepository(AiDbContext dbContext) : IAgentTestRepository
{
    public Task<AgentTest?> FindAsync(Guid tenantId, Guid testId, CancellationToken cancellationToken) =>
        dbContext.Tests.SingleOrDefaultAsync(test => test.TenantId == tenantId && test.Id == testId, cancellationToken);

    public void Add(AgentTest test) => dbContext.Tests.Add(test);

    public void Remove(AgentTest test) => dbContext.Tests.Remove(test);

    public async Task<AgentTestActivity> LoadActivityAsync(Guid tenantId, Guid testId, CancellationToken cancellationToken)
    {
        var jobs = dbContext.ModelJobs.Where(job => job.TenantId == tenantId && job.TestId == testId).Select(job => job.Id);
        var calls = await dbContext.ToolCalls.AsNoTracking()
            .Where(call => call.TenantId == tenantId && jobs.Contains(call.JobId))
            .OrderBy(call => call.Id)
            .ToListAsync(cancellationToken);
        var usage = await dbContext.UsageRecords.AsNoTracking()
            .Where(record => record.TenantId == tenantId && record.TestId == testId)
            .GroupBy(record => record.Provider)
            .Select(group => new
            {
                Provider = group.Key,
                Calls = group.Count(),
                Input = group.Sum(record => (long)record.InputTokens),
                Output = group.Sum(record => (long)record.OutputTokens),
                CacheCreation = group.Sum(record => (long?)record.CacheCreationInputTokens),
                CacheRead = group.Sum(record => (long?)record.CacheReadInputTokens),
            })
            .OrderBy(row => row.Provider)
            .ToListAsync(cancellationToken);

        return new AgentTestActivity(
            [
                .. calls.Select(call => new AgentTestToolCallDto(
                    call.CallId,
                    call.Tool,
                    JsonDocument.Parse(call.Arguments).RootElement.Clone(),
                    call.Result is null ? null : JsonDocument.Parse(call.Result).RootElement.Clone(),
                    call.Outcome,
                    call.ErrorCode,
                    call.DurationMs,
                    call.CreatedAt)),
            ],
            [.. usage.Select(row => new AgentTestUsageDto(row.Provider, row.Calls, row.Input, row.Output, row.CacheCreation, row.CacheRead))]);
    }
}

internal sealed class AiModelJobs(AiDbContext dbContext) : IAiModelJobs
{
    public void Add(ModelJob job) => dbContext.ModelJobs.Add(job);

    /// <summary>
    /// <c>SKIP LOCKED</c>: dos workers (o dos réplicas) nunca toman el mismo trabajo. Un <c>Running</c> con el lease vencido es de un worker
    /// que murió, o uno que dejó su respuesta guardada o su reintento con el lease acortado (§6.3): se retoma (§6.4). De pruebas y de
    /// sesiones. Los del copiloto (spec 2026-11-17) todavía no: hasta que su procesador llegue (plan fase 2, C2.3) quedan en la cola, y
    /// reclamarlos los mandaría al procesador de sesiones sin sesión.
    /// </summary>
    public async Task<ModelJob?> ClaimAsync(DateTimeOffset now, TimeSpan lease, CancellationToken cancellationToken)
    {
        var leaseUntil = now + lease;
        var claimed = await dbContext.Database.SqlQuery<Guid>($"""
            UPDATE ai.model_jobs
               SET status = 'Running', attempts = attempts + 1, lease_until = {leaseUntil}
             WHERE id = (
                   SELECT id FROM ai.model_jobs
                    WHERE ((status = 'Pending' AND next_attempt_at <= {now}) OR (status = 'Running' AND lease_until < {now}))
                      AND copilot_thread_id IS NULL
                    ORDER BY next_attempt_at
                    LIMIT 1
                    FOR UPDATE SKIP LOCKED)
            RETURNING id AS "Value"
            """).ToListAsync(cancellationToken);
        return claimed.Count == 0 ? null : await dbContext.ModelJobs.SingleAsync(job => job.Id == claimed[0], cancellationToken);
    }

    public Task RetryHeldAsync(Guid jobId, DateTimeOffset retryAt, CancellationToken cancellationToken) =>
        dbContext.Database.ExecuteSqlAsync(
            $"UPDATE ai.model_jobs SET lease_until = {retryAt} WHERE id = {jobId} AND status = 'Running'", cancellationToken);
}

/// <summary>
/// <see cref="IAiRunLedger"/> con SQL directo: cada fila en su propio comando, sin pasar por el change tracker, así nunca arrastra otra cosa
/// que el trabajo tenga a medio cambiar (la prueba, el trabajo) ni abre una transacción mientras el modelo piensa (D-A13). Ni la clave ni el
/// texto del cliente llegan aquí: sólo argumentos y resultados de herramientas (P-5) y conteos.
/// </summary>
internal sealed class AiRunLedger(AiDbContext dbContext) : IAiRunLedger
{
    public Task RecordUsageAsync(AiUsageRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        int? cacheCreation = record.Usage.CacheCreation is { } created ? checked((int)created) : null;
        int? cacheRead = record.Usage.CacheRead is { } read ? checked((int)read) : null;
        return dbContext.Database.ExecuteSqlAsync($"""
            INSERT INTO ai.usage_records (tenant_id, agent_id, agent_version_id, conversation_id, session_id, test_id, job_id, provider, model,
                                          served_model, input_tokens, output_tokens, cache_creation_input_tokens, cache_read_input_tokens,
                                          outcome, called_at)
            VALUES ({record.TenantId}, {record.AgentId}, {record.AgentVersionId}, {record.ConversationId}, {record.SessionId}, {record.TestId},
                    {record.JobId}, {record.Provider.Value}, {record.Model}, {record.ServedModel}, {checked((int)record.Usage.Input)},
                    {checked((int)record.Usage.Output)}, {cacheCreation}, {cacheRead}, {record.Outcome}, {record.CalledAt})
            """, cancellationToken);
    }

    /// <summary>Un trabajo retomado después de un lease vencido puede repetir un <c>call_id</c>: la primera fila queda.</summary>
    public Task RecordToolCallAsync(AiToolCallRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        return dbContext.Database.ExecuteSqlAsync($"""
            INSERT INTO ai.tool_calls (tenant_id, job_id, call_id, tool, arguments, result, outcome, error_code, duration_ms, created_at)
            VALUES ({record.TenantId}, {record.JobId}, {Fit(record.CallId, 64)}, {Fit(record.Tool, 40)}, CAST({record.ArgumentsJson} AS jsonb),
                    CAST({record.ResultJson} AS jsonb), {record.Outcome}, {record.ErrorCode}, {record.DurationMs}, {record.CreatedAt})
            ON CONFLICT (job_id, call_id) DO NOTHING
            """, cancellationToken);
    }

    /// <summary>El id y el nombre los inventa el modelo: uno más largo que la columna se corta, no tumba el registro.</summary>
    private static string Fit(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength];
}
