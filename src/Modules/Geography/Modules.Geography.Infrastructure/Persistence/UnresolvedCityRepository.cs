using Microsoft.EntityFrameworkCore;
using Modules.Geography.Application;
using Modules.Geography.Domain;

namespace Modules.Geography.Infrastructure.Persistence;

/// <summary>
/// SQL crudo y no <c>SaveChanges</c>: el upsert apunta al índice parcial <see cref="GeographyDbContext.UnresolvedTextOpenIndex"/>
/// (<c>WHERE status &lt;&gt; 'Resolved'</c>), así que un texto descartado suma a su fila y sigue descartado (D-C9), y uno
/// resuelto cuyo sinónimo se borró abre una fila nueva sin tocar la vieja (§4.7). Cada <c>INSERT … RETURNING</c> se lee
/// entero, sin componer encima: EF lo envolvería en una subconsulta, y Postgres no acepta un <c>INSERT</c> ahí.
/// </summary>
internal sealed class UnresolvedCityRepository(GeographyDbContext dbContext) : IUnresolvedCityRepository
{
    private const string PendingStatus = nameof(UnresolvedCityStatus.Pending);

    public async Task<(UnresolvedCityId Id, bool OccurrenceInserted)> UpsertWithOccurrenceAsync(
        UnresolvedCityReport report, string normalizedText, CancellationToken cancellationToken)
    {
        // Antes del upsert, para no mover la fila (ni abrir otra, si la misma fila de origen vuelve con otro texto) por
        // una ocurrencia que ya estaba. El candado del texto serializa los reintentos con el mismo texto.
        if (await FindOccurrenceOwnerAsync(report, cancellationToken) is { } existing)
        {
            return (existing, false);
        }

        // Npgsql sólo escribe un timestamptz con desfase cero, y la hora de la tienda llega con el suyo.
        var seenAt = report.SeenAt.ToUniversalTime();

        // La muestra y la provincia son las del pedido más nuevo: el recorrido histórico (SC-13) reporta pedidos viejos
        // después de los nuevos, y no deben pisarlas ni mover last_seen_at hacia atrás.
        var rowIds = await dbContext.Database.SqlQuery<Guid>($"""
            INSERT INTO geography.unresolved_cities AS u
                (id, department_id, normalized_text, city_text, sample_address, province_code, status,
                 first_seen_at, last_seen_at, updated_at, version)
            VALUES ({Guid.CreateVersion7()}, {report.DepartmentId.Value}, {normalizedText}, {report.CityText},
                    {report.SampleAddress}, {report.ProvinceCode}, {PendingStatus}, {seenAt}, {seenAt}, {seenAt}, 1)
            ON CONFLICT (department_id, normalized_text) WHERE status <> 'Resolved' DO UPDATE SET
                first_seen_at = LEAST(u.first_seen_at, EXCLUDED.first_seen_at),
                last_seen_at = GREATEST(u.last_seen_at, EXCLUDED.last_seen_at),
                sample_address = CASE WHEN EXCLUDED.last_seen_at >= u.last_seen_at
                    THEN EXCLUDED.sample_address ELSE u.sample_address END,
                province_code = CASE WHEN EXCLUDED.last_seen_at >= u.last_seen_at
                    THEN EXCLUDED.province_code ELSE u.province_code END
            RETURNING id AS "Value"
            """).ToListAsync(cancellationToken);
        var rowId = new UnresolvedCityId(rowIds.Single());

        var occurrenceIds = await dbContext.Database.SqlQuery<Guid>($"""
            INSERT INTO geography.unresolved_city_occurrences
                (id, unresolved_city_id, tenant_id, source, source_ref, order_id, customer_id,
                 party_city_only, customer_city_only, seen_at)
            VALUES ({Guid.CreateVersion7()}, {rowId.Value}, {report.TenantId}, {report.Source}, {report.SourceRef},
                    {report.OrderId}, {report.CustomerId}, {report.PartyCityOnly}, {report.CustomerCityOnly}, {seenAt})
            ON CONFLICT (source, source_ref) DO NOTHING
            RETURNING id AS "Value"
            """).ToListAsync(cancellationToken);
        if (occurrenceIds.Count == 1)
        {
            return (rowId, true);
        }

        // La misma fila de origen llegó a la vez con otro texto (otro candado) y ganó: su ocurrencia ya está commiteada.
        return (await FindOccurrenceOwnerAsync(report, cancellationToken)
            ?? throw new InvalidOperationException(
                $"The occurrence of '{report.Source}' row '{report.SourceRef}' conflicted but could not be read back."),
            false);
    }

    private async Task<UnresolvedCityId?> FindOccurrenceOwnerAsync(
        UnresolvedCityReport report, CancellationToken cancellationToken)
    {
        var owners = await dbContext.Database.SqlQuery<Guid>($"""
            SELECT unresolved_city_id AS "Value"
            FROM geography.unresolved_city_occurrences
            WHERE source = {report.Source} AND source_ref = {report.SourceRef}
            """).ToListAsync(cancellationToken);
        return owners.Count == 0 ? null : new UnresolvedCityId(owners[0]);
    }

    public Task<UnresolvedCity?> FindAsync(UnresolvedCityId id, CancellationToken cancellationToken) =>
        dbContext.UnresolvedCities.SingleOrDefaultAsync(row => row.Id == id, cancellationToken);

    public async Task<(IReadOnlyList<UnresolvedCityRow> Rows, int Total)> ListAsync(
        UnresolvedCityStatus status,
        DepartmentId? departmentId,
        string? normalizedSearch,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var query = dbContext.UnresolvedCities.AsNoTracking().Where(row => row.Status == status);
        if (departmentId is { } department)
        {
            query = query.Where(row => row.DepartmentId == department);
        }

        // Contains se traduce a strpos, así que un % o un _ del buscador son texto y no comodines.
        if (!string.IsNullOrEmpty(normalizedSearch))
        {
            query = query.Where(row => row.NormalizedText.Contains(normalizedSearch));
        }

        var total = await query.CountAsync(cancellationToken);
        var rows = await query
            .OrderByDescending(row => row.LastSeenAt)
            .ThenByDescending(row => row.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(row => new
            {
                City = row,
                OrderCount = dbContext.UnresolvedCityOccurrences.Count(occurrence => occurrence.UnresolvedCityId == row.Id),
                TenantCount = dbContext.UnresolvedCityOccurrences
                    .Where(occurrence => occurrence.UnresolvedCityId == row.Id)
                    .Select(occurrence => occurrence.TenantId)
                    .Distinct()
                    .Count(),
            })
            .ToListAsync(cancellationToken);
        return (rows.Select(row => new UnresolvedCityRow(row.City, row.OrderCount, row.TenantCount)).ToList(), total);
    }

    public async Task<IReadOnlyList<UnresolvedCityOccurrence>> ListOccurrencesAsync(
        UnresolvedCityId id, CancellationToken cancellationToken) =>
        await dbContext.UnresolvedCityOccurrences
            .AsNoTracking()
            .Where(occurrence => occurrence.UnresolvedCityId == id)
            .OrderBy(occurrence => occurrence.SeenAt)
            .ThenBy(occurrence => occurrence.Id)
            .ToListAsync(cancellationToken);

    public async Task<CitySummaryCounts> CountAsync(CancellationToken cancellationToken)
    {
        var pending = await dbContext.UnresolvedCities.CountAsync(
            row => row.Status == UnresolvedCityStatus.Pending, cancellationToken);
        var discarded = await dbContext.UnresolvedCities.CountAsync(
            row => row.Status == UnresolvedCityStatus.Discarded, cancellationToken);
        var synonyms = await dbContext.CitySynonyms.CountAsync(synonym => synonym.DeletedAt == null, cancellationToken);
        var waiting = await dbContext.UnresolvedCityOccurrences.CountAsync(
            occurrence => dbContext.UnresolvedCities.Any(row =>
                row.Id == occurrence.UnresolvedCityId && row.Status == UnresolvedCityStatus.Pending),
            cancellationToken);
        return new CitySummaryCounts(pending, discarded, synonyms, waiting);
    }
}
