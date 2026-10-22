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

    public async Task AddAsync(Chatbot chatbot, CancellationToken cancellationToken) =>
        await dbContext.Chatbots.AddAsync(chatbot, cancellationToken);

    public void Remove(Chatbot chatbot) => dbContext.Chatbots.Remove(chatbot);

    // La columna usa ToLower() sin argumentos: es lo único que Npgsql traduce a lower(), la misma expresión de IX_chatbots_tenant_name
    // (ToLowerInvariant y Equals(OrdinalIgnoreCase) no se traducen; precedente: BusinessHoursRepository). El lado del parámetro se
    // evalúa en .NET, así que va invariante. El pragma cubre sólo esa expresión.
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
