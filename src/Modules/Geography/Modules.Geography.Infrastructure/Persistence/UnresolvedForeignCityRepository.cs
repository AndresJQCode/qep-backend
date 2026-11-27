using Microsoft.EntityFrameworkCore;
using Modules.Geography.Application;
using Modules.Geography.Domain;

namespace Modules.Geography.Infrastructure.Persistence;

/// <summary>
/// SQL crudo y no <c>SaveChanges</c>: el upsert apunta al índice parcial <see cref="GeographyDbContext.UnresolvedForeignTextOpenIndex"/>
/// (<c>WHERE status &lt;&gt; 'Resolved'</c>), así que un texto descartado suma a su fila y sigue descartado, y uno resuelto
/// cuyo sinónimo se borró abre una fila nueva sin tocar la vieja. Cada <c>INSERT … RETURNING</c> se lee entero, sin componer
/// encima: EF lo envolvería en una subconsulta, y Postgres no acepta un <c>INSERT</c> ahí.
/// </summary>
internal sealed class UnresolvedForeignCityRepository(GeographyDbContext dbContext) : IUnresolvedForeignCityRepository
{
    private const string PendingStatus = nameof(UnresolvedCityStatus.Pending);

    public async Task<(UnresolvedForeignCityId Id, bool OccurrenceInserted)> UpsertWithOccurrenceAsync(
        UnresolvedForeignCity candidate, Guid tenantId, Guid orderId, CancellationToken cancellationToken)
    {
        // Npgsql sólo escribe un timestamptz con desfase cero.
        var seenAt = candidate.LastSeenAt.ToUniversalTime();

        var rowIds = await dbContext.Database.SqlQuery<Guid>($"""
            INSERT INTO geography.unresolved_foreign_cities AS u
                (id, country_code, normalized_text, city_text, status, first_seen_at, last_seen_at, updated_at, version)
            VALUES ({Guid.CreateVersion7()}, {candidate.CountryCode}, {candidate.NormalizedText}, {candidate.CityText},
                    {PendingStatus}, {seenAt}, {seenAt}, {seenAt}, 1)
            ON CONFLICT (country_code, normalized_text) WHERE status <> 'Resolved' DO UPDATE SET
                first_seen_at = LEAST(u.first_seen_at, EXCLUDED.first_seen_at),
                last_seen_at = GREATEST(u.last_seen_at, EXCLUDED.last_seen_at)
            RETURNING id AS "Value"
            """).ToListAsync(cancellationToken);
        var rowId = new UnresolvedForeignCityId(rowIds.Single());

        var occurrenceIds = await dbContext.Database.SqlQuery<Guid>($"""
            INSERT INTO geography.unresolved_foreign_city_occurrences (id, unresolved_city_id, tenant_id, order_id, seen_at)
            VALUES ({Guid.CreateVersion7()}, {rowId.Value}, {tenantId}, {orderId}, {seenAt})
            ON CONFLICT (unresolved_city_id, order_id) DO NOTHING
            RETURNING id AS "Value"
            """).ToListAsync(cancellationToken);

        // Sin fila devuelta, el pedido ya tenía su ocurrencia: quien llama no confirma y el rollback deshace el
        // last_seen_at que el upsert acaba de mover.
        return (rowId, occurrenceIds.Count == 1);
    }

    public Task<UnresolvedForeignCity?> FindAsync(UnresolvedForeignCityId id, CancellationToken cancellationToken) =>
        dbContext.UnresolvedForeignCities.SingleOrDefaultAsync(row => row.Id == id, cancellationToken);

    public Task RefreshAsync(UnresolvedForeignCity city, CancellationToken cancellationToken) =>
        dbContext.Entry(city).ReloadAsync(cancellationToken);

    public async Task<(IReadOnlyList<UnresolvedForeignCityRow> Rows, int Total)> ListAsync(
        UnresolvedCityStatus status,
        string? countryCode,
        string? normalizedSearch,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var query = dbContext.UnresolvedForeignCities.AsNoTracking().Where(row => row.Status == status);
        if (countryCode is not null)
        {
            query = query.Where(row => row.CountryCode == countryCode);
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
                OrderCount = dbContext.UnresolvedForeignCityOccurrences.Count(occurrence => occurrence.UnresolvedCityId == row.Id),
                TenantCount = dbContext.UnresolvedForeignCityOccurrences
                    .Where(occurrence => occurrence.UnresolvedCityId == row.Id)
                    .Select(occurrence => occurrence.TenantId)
                    .Distinct()
                    .Count(),
            })
            .ToListAsync(cancellationToken);
        return (rows.Select(row => new UnresolvedForeignCityRow(row.City, row.OrderCount, row.TenantCount)).ToList(), total);
    }

    public async Task<IReadOnlyList<UnresolvedForeignCityOccurrence>> ListOccurrencesAsync(
        UnresolvedForeignCityId id, CancellationToken cancellationToken) =>
        await dbContext.UnresolvedForeignCityOccurrences
            .AsNoTracking()
            .Where(occurrence => occurrence.UnresolvedCityId == id)
            .OrderBy(occurrence => occurrence.SeenAt)
            .ThenBy(occurrence => occurrence.Id)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<string, int>> CountPendingByCountryAsync(CancellationToken cancellationToken) =>
        await dbContext.UnresolvedForeignCities
            .AsNoTracking()
            .Where(row => row.Status == UnresolvedCityStatus.Pending)
            .GroupBy(row => row.CountryCode)
            .Select(group => new { Country = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.Country, item => item.Count, StringComparer.Ordinal, cancellationToken);
}
