using Microsoft.EntityFrameworkCore;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.Infrastructure.Persistence;

internal sealed class QuotationRepository(QuotationsDbContext dbContext) : IQuotationRepository
{
    // Con tracking a proposito: los llamadores mutan el agregado y dependen de la unidad de
    // trabajo para persistirlo. El Include es obligatorio por dos razones, no solo una: sin el,
    // Items llega vacio en cada lectura, y sin las lineas viejas en el change tracker un
    // RemoveItem no las ve borradas. Mismo criterio que ProductRepository.FindAsync con
    // PriceScales.
    public Task<Quotation?> FindAsync(
        Guid tenantId, QuotationId quotationId, CancellationToken cancellationToken) =>
        dbContext.Quotations
            .Include(quotation => quotation.Items)
            .SingleOrDefaultAsync(
                quotation => quotation.TenantId == tenantId && quotation.Id == quotationId,
                cancellationToken);

    // AsNoTracking y sin Include(Items) a propósito: el listado (US-8) sólo pinta encabezado --
    // número, cliente, asesora, fecha, total, estado -- nunca líneas de producto, así que
    // traerlas acá sería el mismo N+1 innecesario que ProductRepository.SearchAsync evita no
    // trayendo lo que la fila no muestra.
    private const string LikeEscapeCharacter = "\\";

    // Mismo criterio que CustomerRepository.EscapeLikeWildcards/LikePattern: un numero de
    // cotizacion buscado que por casualidad trae "%" o "_" no debe actuar como comodin SQL.
    private static string EscapeLikeWildcards(string term) => term
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);

    private static string? LikePattern(string? term)
    {
        var trimmed = term?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : $"%{EscapeLikeWildcards(trimmed)}%";
    }

    public async Task<(IReadOnlyList<Quotation> Items, int Total)> SearchAsync(
        Guid tenantId,
        Guid? clientId,
        IReadOnlyCollection<Guid>? clientIds,
        MemberId? advisorId,
        QuotationStatus? status,
        DateOnly? createdFrom,
        DateOnly? createdTo,
        string? quotationNumber,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var query = dbContext.Quotations
            .AsNoTracking()
            .Where(quotation => quotation.TenantId == tenantId);

        if (clientId is { } client)
        {
            query = query.Where(quotation => quotation.ClientId == client);
        }

        // `null` = sin filtro por NIT; una coleccion (incluso vacia, cuando el NIT buscado no
        // resolvio a ningun cliente) filtra por esos ids exactos.
        if (clientIds is not null)
        {
            query = query.Where(quotation => clientIds.Contains(quotation.ClientId));
        }

        var quotationNumberPattern = LikePattern(quotationNumber);
        if (quotationNumberPattern is not null)
        {
            query = query.Where(quotation =>
                EF.Functions.ILike(quotation.QuotationNumber, quotationNumberPattern, LikeEscapeCharacter));
        }

        if (advisorId is { } advisor)
        {
            query = query.Where(quotation => quotation.AdvisorId == advisor);
        }

        if (status is { } quotationStatus)
        {
            query = query.Where(quotation => quotation.Status == quotationStatus);
        }

        if (createdFrom is { } from)
        {
            var fromUtc = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            query = query.Where(quotation => quotation.CreatedAt >= fromUtc);
        }

        if (createdTo is { } to)
        {
            // Limite superior exclusivo al dia siguiente: "hasta el 30" incluye todo el 30,
            // no solo el instante 00:00:00 de esa fecha.
            var toUtcExclusive = new DateTimeOffset(
                to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            query = query.Where(quotation => quotation.CreatedAt < toUtcExclusive);
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(quotation => quotation.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items, total);
    }

    public void Add(Quotation quotation) => dbContext.Quotations.Add(quotation);

    public void AddHistoryEntry(QuotationHistoryEntry entry) =>
        dbContext.QuotationHistoryEntries.Add(entry);
}
