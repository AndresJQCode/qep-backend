using Microsoft.EntityFrameworkCore;
using Modules.Automations.Application;

namespace Modules.Automations.Infrastructure.Persistence;

/// <summary>Estados de <c>conversation_runs.status</c> que estas consultas leen (los mismos de <c>LiveRunFilter</c>).</summary>
internal static class RunStatuses
{
    public const string Running = "Running";
    public const string Waiting = "Waiting";
    public const string HandedOff = "HandedOff";
}

/// <summary>SQL sobre <c>automations.conversation_runs</c>: el registro es interno de Infrastructure, así que Application lo lee por
/// este puerto. La ventana del día ya viene en UTC desde el reloj del tenant.</summary>
internal sealed class ConversationRunQueries(AutomationsDbContext dbContext) : IConversationRunQueries
{
    public async Task<IReadOnlyDictionary<Guid, (int Handled, int HandedOff)>> TodayAsync(
        Guid tenantId, IReadOnlyCollection<Guid> chatbotIds, DateTimeOffset dayStartUtc, DateTimeOffset dayEndUtc,
        CancellationToken cancellationToken)
    {
        if (chatbotIds.Count == 0)
        {
            return new Dictionary<Guid, (int, int)>();
        }

        var ids = chatbotIds.ToArray();
        // Npgsql rechaza un offset distinto de cero en timestamptz.
        var windowStart = dayStartUtc.ToUniversalTime();
        var windowEnd = dayEndUtc.ToUniversalTime();
        var handedOff = RunStatuses.HandedOff;
        var rows = await dbContext.Database.SqlQuery<TodayRow>($"""
            SELECT chatbot_id AS "ChatbotId",
                   count(*)::int AS "Handled",
                   (count(*) FILTER (WHERE status = {handedOff}))::int AS "HandedOff"
            FROM automations.conversation_runs
            WHERE tenant_id = {tenantId}
              AND chatbot_id = ANY({ids})
              AND started_at >= {windowStart}
              AND started_at < {windowEnd}
            GROUP BY chatbot_id
            """).ToListAsync(cancellationToken);
        return rows.ToDictionary(row => row.ChatbotId, row => (row.Handled, row.HandedOff));
    }

    public Task<bool> HasLiveRunsAsync(Guid tenantId, Guid chatbotId, CancellationToken cancellationToken) =>
        dbContext.Runs.AsNoTracking().AnyAsync(
            run => run.TenantId == tenantId && run.ChatbotId == chatbotId && (run.Status == RunStatuses.Running || run.Status == RunStatuses.Waiting),
            cancellationToken);

    // Clase con setters y no un record: EF no ve a través del constructor de un record en SqlQuery.
    private sealed class TodayRow
    {
        public Guid ChatbotId { get; init; }

        public int Handled { get; init; }

        public int HandedOff { get; init; }
    }
}
