using Microsoft.EntityFrameworkCore;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.Infrastructure.Persistence;

internal sealed class QuotationRepository(QuotationsDbContext dbContext) : IQuotationRepository
{
    // Con tracking a proposito: los llamadores mutan el agregado y dependen de la unidad de
    // trabajo para persistirlo. El Include es obligatorio por dos razones, no solo una: sin el,
    // Items llega vacio en cada lectura, y sin las lineas viejas en el change tracker un
    // RemoveItem no las ve borradas. Lo mismo vale para Parties: sin la fila vieja trackeada,
    // volver a prender el switch ("usa los datos del cliente") no borraria nada. Mismo criterio que ProductRepository.FindAsync con
    // PriceScales.
    public Task<Quotation?> FindAsync(
        Guid tenantId, QuotationId quotationId, CancellationToken cancellationToken) =>
        dbContext.Quotations
            .Include(quotation => quotation.Items)
            .Include(quotation => quotation.Parties)
            .SingleOrDefaultAsync(
                quotation => quotation.TenantId == tenantId && quotation.Id == quotationId,
                cancellationToken);

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
        var query = FilteredQuery(
            tenantId, clientId, clientIds, advisorId, status, createdFrom, createdTo, quotationNumber);

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(quotation => quotation.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items, total);
    }

    // Sin Count ni paginacion: el Excel se lleva todo lo que el filtro deja pasar, en el mismo
    // orden que la tabla.
    public async Task<IReadOnlyList<Quotation>> ListForExportAsync(
        Guid tenantId,
        Guid? clientId,
        IReadOnlyCollection<Guid>? clientIds,
        MemberId? advisorId,
        QuotationStatus? status,
        DateOnly? createdFrom,
        DateOnly? createdTo,
        string? quotationNumber,
        CancellationToken cancellationToken) =>
        await FilteredQuery(
                tenantId, clientId, clientIds, advisorId, status, createdFrom, createdTo, quotationNumber)
            .OrderByDescending(quotation => quotation.CreatedAt)
            .ToListAsync(cancellationToken);

    // Los filtros del listado y de su Excel salen de aca y de ningun otro lado: si cada camino
    // armara los suyos, tarde o temprano el archivo diria algo distinto de la pantalla.
    //
    // AsNoTracking y sin Include(Items) a propósito: el listado (US-8) sólo pinta encabezado --
    // número, cliente, asesora, fecha, total, estado -- nunca líneas de producto, así que
    // traerlas acá sería el mismo N+1 innecesario que ProductRepository.SearchAsync evita no
    // trayendo lo que la fila no muestra.
    private IQueryable<Quotation> FilteredQuery(
        Guid tenantId,
        Guid? clientId,
        IReadOnlyCollection<Guid>? clientIds,
        MemberId? advisorId,
        QuotationStatus? status,
        DateOnly? createdFrom,
        DateOnly? createdTo,
        string? quotationNumber)
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

        return query;
    }

    // Devuelve ids y no lineas: la pregunta es "tiene al menos una", y traer las lineas para
    // contarlas seria cargar toda la pagina de items para descartarlos. `Items.Any()` se traduce
    // a un EXISTS por fila dentro de la misma consulta, filtrado por tenant como todo lo demas.
    public async Task<IReadOnlySet<Guid>> FindIdsWithItemsAsync(
        Guid tenantId,
        IReadOnlyCollection<QuotationId> quotationIds,
        CancellationToken cancellationToken)
    {
        if (quotationIds.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var ids = quotationIds.ToArray();
        var found = await dbContext.Quotations
            .AsNoTracking()
            .Where(quotation => quotation.TenantId == tenantId
                && ids.Contains(quotation.Id)
                && quotation.Items.Any())
            .Select(quotation => quotation.Id)
            .ToListAsync(cancellationToken);

        return found.Select(id => id.Value).ToHashSet();
    }

    public void Add(Quotation quotation) => dbContext.Quotations.Add(quotation);

    public void AddHistoryEntry(QuotationHistoryEntry entry) =>
        dbContext.QuotationHistoryEntries.Add(entry);

    // Con tracking, a diferencia de ListHistoryAsync: quien lo pide lo hace para decidir si
    // regenerarlo, y en ese caso llama a Regenerate() sobre esta misma instancia.
    //
    // El tenant se filtra por columna propia y no por join con la cotizacion --como si hace el
    // historial-- porque quotation_pdfs si lleva tenant_id: la copia publica se arma con el, asi
    // que tenerlo a mano evita cargar la cotizacion entera solo para leerlo.
    public Task<QuotationPdf?> FindPdfAsync(
        Guid tenantId, QuotationId quotationId, CancellationToken cancellationToken) =>
        dbContext.QuotationPdfs
            .FirstOrDefaultAsync(
                pdf => pdf.QuotationId == quotationId && pdf.TenantId == tenantId,
                cancellationToken);

    public void AddPdf(QuotationPdf pdf) => dbContext.QuotationPdfs.Add(pdf);

    // El filtro de tenant va por la cotizacion y no por la entrada: quotation_history no lleva
    // tenant_id propio -- es hija de una cotizacion que si lo lleva. Sin este join, un id de otro
    // tenant devolveria su historial.
    public async Task<IReadOnlyList<QuotationHistoryEntry>> ListHistoryAsync(
        Guid tenantId, QuotationId quotationId, CancellationToken cancellationToken) =>
        await dbContext.QuotationHistoryEntries
            .AsNoTracking()
            .Where(entry => entry.QuotationId == quotationId
                && dbContext.Quotations.Any(quotation =>
                    quotation.Id == quotationId && quotation.TenantId == tenantId))
            .OrderByDescending(entry => entry.EventAt)
            .ThenByDescending(entry => entry.CreatedAt)
            .ToListAsync(cancellationToken);
}
