using Microsoft.EntityFrameworkCore;
using Modules.Automations.Application;
using Modules.Automations.Domain;

namespace Modules.Automations.Infrastructure.Persistence;

internal sealed class ChatbotRepository(AutomationsDbContext dbContext) : IChatbotRepository
{
    public Task<Chatbot?> FindAsync(Guid tenantId, Guid id, CancellationToken cancellationToken) =>
        WithChildren().SingleOrDefaultAsync(chatbot => chatbot.TenantId == tenantId && chatbot.Id == id, cancellationToken);

    public async Task<IReadOnlyList<Chatbot>> ListAsync(Guid tenantId, string? search, CancellationToken cancellationToken)
    {
        var query = WithChildren().Where(chatbot => chatbot.TenantId == tenantId);
        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(chatbot => EF.Functions.ILike(chatbot.Name, $"%{EscapeLike(search.Trim())}%", "\\"));
        }

        return await query.OrderBy(chatbot => chatbot.Name).ThenBy(chatbot => chatbot.Id).ToListAsync(cancellationToken);
    }

    public Task<Chatbot?> FindPublishedByConnectionAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        WithChildren().SingleOrDefaultAsync(
            chatbot => chatbot.TenantId == tenantId
                && chatbot.PublishedVersionId != null
                && chatbot.Connections.Any(connection => connection.ConnectionId == connectionId && connection.Active),
            cancellationToken);

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

    private IQueryable<Chatbot> WithChildren() =>
        dbContext.Chatbots.Include(chatbot => chatbot.Versions).Include(chatbot => chatbot.Connections);

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
