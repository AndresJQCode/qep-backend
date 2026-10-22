using Microsoft.EntityFrameworkCore;
using Modules.Automations.Application;
using Modules.Automations.Domain;
using Modules.Automations.Domain.Engine;

namespace Modules.Automations.Infrastructure.Persistence;

internal sealed class ChatbotRepository(AutomationsDbContext dbContext) : IChatbotRepository
{
    public Task<Chatbot?> FindAsync(Guid tenantId, Guid id, CancellationToken cancellationToken) =>
        WithChildren().SingleOrDefaultAsync(chatbot => chatbot.TenantId == tenantId && chatbot.Id == id, cancellationToken);

    public Task<Chatbot?> FindPublishedByConnectionAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        WithChildren().SingleOrDefaultAsync(
            chatbot => chatbot.TenantId == tenantId
                && chatbot.PublishedVersionId != null
                && chatbot.Connections.Any(connection => connection.ConnectionId == connectionId && connection.Active),
            cancellationToken);

    // Ruling P2-R6: el motor consulta esto por cada entrante sin corrida; el agregado traería el flujo de todas las versiones.
    public Task<ServingChatbot?> FindServingByConnectionAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        dbContext.Chatbots.AsNoTracking()
            .Where(chatbot => chatbot.TenantId == tenantId
                && chatbot.PublishedVersionId != null
                && chatbot.Connections.Any(connection => connection.ConnectionId == connectionId && connection.Active))
            .Select(chatbot => new ServingChatbot(
                chatbot.Id, chatbot.Name, chatbot.Status, chatbot.Trigger, chatbot.Keywords, chatbot.RestartKeyword, chatbot.MaxAttempts,
                chatbot.PublishedVersionId!.Value))
            .SingleOrDefaultAsync(cancellationToken);

    public Task<string?> GetVersionFlowAsync(Guid tenantId, Guid versionId, CancellationToken cancellationToken) =>
        dbContext.ChatbotVersions.AsNoTracking()
            .Where(version => version.TenantId == tenantId && version.Id == versionId)
            .Select(version => (string?)version.FlowJson)
            .SingleOrDefaultAsync(cancellationToken);

    public Task<EngineSettings?> FindEngineSettingsAsync(Guid tenantId, Guid chatbotId, CancellationToken cancellationToken) =>
        dbContext.Chatbots.AsNoTracking()
            .Where(chatbot => chatbot.TenantId == tenantId && chatbot.Id == chatbotId)
            .Select(chatbot => new EngineSettings(chatbot.RestartKeyword, chatbot.MaxAttempts))
            .SingleOrDefaultAsync(cancellationToken);

    // Proyección: la lista no necesita el borrador ni el flujo de cada versión publicada (jsonb de hasta 512 KB cada uno).
    public async Task<IReadOnlyList<ChatbotSummaryRow>> ListSummariesAsync(Guid tenantId, string? search, CancellationToken cancellationToken)
    {
        var query = dbContext.Chatbots.AsNoTracking().Where(chatbot => chatbot.TenantId == tenantId);
        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(chatbot => EF.Functions.ILike(chatbot.Name, $"%{EscapeLike(search.Trim())}%", "\\"));
        }

        return await query
            .OrderBy(chatbot => chatbot.Name)
            .ThenBy(chatbot => chatbot.Id)
            .Select(chatbot => new ChatbotSummaryRow(
                chatbot.Id,
                chatbot.Name,
                chatbot.Description,
                chatbot.Status,
                chatbot.Trigger,
                chatbot.Connections.Select(connection => connection.ConnectionId).ToList(),
                chatbot.Versions.Where(version => version.Id == chatbot.PublishedVersionId).Select(version => (int?)version.Number).FirstOrDefault(),
                chatbot.Versions.Where(version => version.Id == chatbot.PublishedVersionId).Select(version => (DateTimeOffset?)version.PublishedAt).FirstOrDefault(),
                chatbot.DraftUpdatedAt,
                chatbot.Version))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ChatbotServingConnection>> ListServingConnectionsAsync(Guid tenantId, CancellationToken cancellationToken) =>
        await dbContext.Chatbots.AsNoTracking()
            .Where(chatbot => chatbot.TenantId == tenantId && chatbot.PublishedVersionId != null)
            .SelectMany(
                chatbot => chatbot.Connections.Where(connection => connection.Active),
                (chatbot, connection) => new ChatbotServingConnection(connection.ConnectionId, chatbot.Id, chatbot.Name))
            .ToListAsync(cancellationToken);

    public async Task AddAsync(Chatbot chatbot, CancellationToken cancellationToken) =>
        await dbContext.Chatbots.AddAsync(chatbot, cancellationToken);

    public void Remove(Chatbot chatbot) => dbContext.Chatbots.Remove(chatbot);

    // La columna usa ToLower() sin argumentos: es lo único que Npgsql traduce a lower(), la misma expresión de IX_chatbots_tenant_name
    // (ToLowerInvariant y Equals(OrdinalIgnoreCase) no se traducen; precedente: BusinessHoursRepository). El lado del parámetro se
    // evalúa en .NET, así que va invariante: ToLowerInvariant de .NET y lower() de Postgres pueden diferir en caracteres exóticos y el
    // índice único es el respaldo. El pragma cubre sólo esa expresión.
    public Task<bool> NameExistsAsync(Guid tenantId, string name, Guid? exceptId, CancellationToken cancellationToken)
    {
        var lowered = name.Trim().ToLowerInvariant();
#pragma warning disable CA1304, CA1311, CA1862
        return dbContext.Chatbots.AsNoTracking().AnyAsync(
            chatbot => chatbot.TenantId == tenantId && chatbot.Name.ToLower() == lowered && (exceptId == null || chatbot.Id != exceptId),
            cancellationToken);
#pragma warning restore CA1304, CA1311, CA1862
    }

    // AsSplitQuery: en una sola consulta el JOIN sale versiones × conexiones filas, y cada una repite el flow y el layout (jsonb) de su
    // versión y el borrador del chatbot. Separadas, cada versión viaja una vez. Que el agregado siga trayendo los jsonb de todas las
    // versiones publicadas es deuda conocida (final-report del plan 1): sacarlos pide partir la tabla en el modelo.
    private IQueryable<Chatbot> WithChildren() =>
        dbContext.Chatbots.Include(chatbot => chatbot.Versions).Include(chatbot => chatbot.Connections).AsSplitQuery();

    // El nombre del usuario no puede inyectar comodines: se escapan \, % y _ (ESCAPE '\').
    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
}

internal sealed class ChatbotTemplateRepository(AutomationsDbContext dbContext) : IChatbotTemplateRepository
{
    public Task<ChatbotTemplate?> FindAsync(Guid tenantId, Guid id, CancellationToken cancellationToken) =>
        dbContext.Templates.SingleOrDefaultAsync(template => template.TenantId == tenantId && template.Id == id, cancellationToken);

    public async Task<IReadOnlyList<ChatbotTemplate>> ListAsync(Guid tenantId, CancellationToken cancellationToken) =>
        await dbContext.Templates.AsNoTracking()
            .Where(template => template.TenantId == tenantId)
            .OrderBy(template => template.Name)
            .ThenBy(template => template.Id)
            .ToListAsync(cancellationToken);

    public async Task AddAsync(ChatbotTemplate chatbotTemplate, CancellationToken cancellationToken) =>
        await dbContext.Templates.AddAsync(chatbotTemplate, cancellationToken);

    public void Remove(ChatbotTemplate chatbotTemplate) => dbContext.Templates.Remove(chatbotTemplate);
}

/// <summary>Spec 2026-10-21 §5.2, D-M12 y D-M13. <c>FOR UPDATE</c> y <c>SKIP LOCKED</c> van por SQL crudo sobre la entidad mapeada
/// (EF no los modela) para que el resultado quede rastreado y el procesador guarde con un solo <c>SaveChanges</c>.</summary>
internal sealed class ConversationRunRepository(AutomationsDbContext dbContext) : IConversationRunRepository
{
    private const int ListMax = 500;

    public async Task<ConversationRun?> FindLiveForUpdateAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken)
    {
        RequireTransaction();
        var running = nameof(RunStatus.Running);
        var waiting = nameof(RunStatus.Waiting);
        var rows = await dbContext.Runs.FromSql($"""
            SELECT * FROM automations.conversation_runs
            WHERE tenant_id = {tenantId} AND conversation_id = {conversationId} AND status IN ({running}, {waiting})
            FOR UPDATE
            """).ToListAsync(cancellationToken);
        return rows.SingleOrDefault();
    }

    // Sin transacción el lock de fila se suelta al terminar la sentencia y otro barrido o entrante tomaría la misma fila sin avisar.
    private void RequireTransaction()
    {
        if (dbContext.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Las lecturas FOR UPDATE exigen la transacción de BeginTransactionAsync.");
        }
    }

    public Task<ConversationRun?> FindLatestAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken) =>
        dbContext.Runs.AsNoTracking()
            .Where(run => run.TenantId == tenantId && run.ConversationId == conversationId)
            .OrderByDescending(run => run.StartedAt)
            .ThenByDescending(run => run.Id)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task AddAsync(ConversationRun run, CancellationToken cancellationToken) =>
        await dbContext.Runs.AddAsync(run, cancellationToken);

    public async Task<IReadOnlyList<ConversationRun>> ListAsync(
        Guid tenantId, Guid chatbotId, DateTimeOffset from, DateTimeOffset until, CancellationToken cancellationToken)
    {
        // Npgsql rechaza un offset distinto de cero en timestamptz.
        var start = from.ToUniversalTime();
        var end = until.ToUniversalTime();
        return await dbContext.Runs.AsNoTracking()
            .Where(run => run.TenantId == tenantId && run.ChatbotId == chatbotId && run.StartedAt >= start && run.StartedAt < end)
            .OrderByDescending(run => run.StartedAt)
            .ThenByDescending(run => run.Id)
            .Take(ListMax)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ConversationRun>> ClaimAbandonedAsync(
        DateTimeOffset waitingBefore, int limit, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        RequireTransaction();

        var waiting = nameof(RunStatus.Waiting);
        var cutoff = waitingBefore.ToUniversalTime();
        return await dbContext.Runs.FromSql($"""
            SELECT * FROM automations.conversation_runs
            WHERE status = {waiting} AND updated_at < {cutoff}
            ORDER BY updated_at
            LIMIT {limit}
            FOR UPDATE SKIP LOCKED
            """).ToListAsync(cancellationToken);
    }
}
