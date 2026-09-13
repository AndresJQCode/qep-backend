using Microsoft.EntityFrameworkCore;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.Infrastructure.Persistence;

internal sealed class SaleRepository(QuotationsDbContext dbContext) : ISaleRepository
{
    private const string LikeEscapeCharacter = "\\";

    // Mismo criterio que QuotationRepository: un numero de venta buscado que por casualidad trae
    // "%" o "_" no debe actuar como comodin SQL.
    private static string EscapeLikeWildcards(string term) => term
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);

    private static string? LikePattern(string? term)
    {
        var trimmed = term?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : $"%{EscapeLikeWildcards(trimmed)}%";
    }

    public Task<Sale?> FindByQuotationIdAsync(
        Guid tenantId, QuotationId quotationId, CancellationToken cancellationToken) =>
        dbContext.Sales
            .Include(sale => sale.PaymentProofs)
            .SingleOrDefaultAsync(
                sale => sale.TenantId == tenantId && sale.QuotationId == quotationId,
                cancellationToken);

    // AsNoTracking y sin comprobantes: esto alimenta una fila de listado, que solo pregunta si
    // la cotizacion ya se convirtio y como quedo esa venta. La relacion es 1:1, asi que indexar
    // por cotizacion no puede perder filas.
    public async Task<IReadOnlyDictionary<Guid, Sale>> FindByQuotationIdsAsync(
        Guid tenantId,
        IReadOnlyCollection<QuotationId> quotationIds,
        CancellationToken cancellationToken)
    {
        if (quotationIds.Count == 0)
        {
            return new Dictionary<Guid, Sale>();
        }

        var ids = quotationIds.ToArray();
        var sales = await dbContext.Sales
            .AsNoTracking()
            .Where(sale => sale.TenantId == tenantId && ids.Contains(sale.QuotationId))
            .ToListAsync(cancellationToken);

        return sales.ToDictionary(sale => sale.QuotationId.Value);
    }

    public Task<Sale?> FindByIdAsync(
        Guid tenantId, SaleId saleId, CancellationToken cancellationToken) =>
        dbContext.Sales
            .Include(sale => sale.PaymentProofs)
            .SingleOrDefaultAsync(
                sale => sale.TenantId == tenantId && sale.Id == saleId,
                cancellationToken);

    public async Task<(IReadOnlyList<SaleWithQuotation> Items, int Total)> SearchAsync(
        Guid tenantId,
        Guid? clientId,
        IReadOnlyCollection<Guid>? clientIds,
        MemberId? advisorId,
        SaleStatus? status,
        SalePaymentStatus? paymentStatus,
        DateOnly? convertedFrom,
        DateOnly? convertedTo,
        string? saleNumber,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        // El join va acá adentro y no en el composition root --como sí lo hace el reporte de
        // ventas-- porque las dos tablas son de este módulo y viven en el mismo DbContext.
        var (sales, quotations) = Filtered(
            tenantId, clientId, clientIds, advisorId, status, paymentStatus, convertedFrom, convertedTo, saleNumber);
        var joined =
            from sale in sales
            join quotation in quotations on sale.QuotationId equals quotation.Id
            select new { sale, quotation };

        var total = await joined.CountAsync(cancellationToken);
        var items = await joined
            // Por fecha de conversión, y el número como desempate: dos ventas del mismo instante
            // --el mismo segundo en una siembra de prueba-- tienen que paginar en un orden
            // estable, o una fila puede repetirse o saltarse entre páginas.
            .OrderByDescending(row => row.sale.ConvertedAt)
            .ThenByDescending(row => row.sale.SaleNumber)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(row => new SaleWithQuotation(row.sale, row.quotation))
            .ToListAsync(cancellationToken);

        return (items, total);
    }

    public Task<bool> AnyForExportAsync(
        Guid tenantId,
        Guid? clientId,
        IReadOnlyCollection<Guid>? clientIds,
        MemberId? advisorId,
        SaleStatus? status,
        SalePaymentStatus? paymentStatus,
        DateOnly? convertedFrom,
        DateOnly? convertedTo,
        string? saleNumber,
        CancellationToken cancellationToken)
    {
        var (sales, quotations) = Filtered(
            tenantId, clientId, clientIds, advisorId, status, paymentStatus, convertedFrom, convertedTo, saleNumber);
        return (from sale in sales
                join quotation in quotations on sale.QuotationId equals quotation.Id
                select sale.Id)
            .AnyAsync(cancellationToken);
    }

    // Keyset sobre el orden exacto del listado —(ConvertedAt, SaleNumber), los dos descendentes—:
    // el lote siguiente es "lo que viene después de la última venta leída" y no un offset, así que
    // una venta convertida o que sale del filtro durante el export no repite ni salta filas
    // (spec 2026-09-12, D8). El corte va sobre `sales`, antes del join, igual que los filtros, y es
    // una comparación de filas de Postgres, `(converted_at, sale_number) < (@fecha, @numero)`, que
    // Npgsql traduce desde EF.Functions.LessThan: IX_sales_tenant_converted_at_number la resuelve
    // como un rango, y el número se compara con la collation de la columna, la misma del ORDER BY.
    public async Task<IReadOnlyList<SaleWithQuotation>> ListForExportAsync(
        Guid tenantId,
        Guid? clientId,
        IReadOnlyCollection<Guid>? clientIds,
        MemberId? advisorId,
        SaleStatus? status,
        SalePaymentStatus? paymentStatus,
        DateOnly? convertedFrom,
        DateOnly? convertedTo,
        string? saleNumber,
        SaleExportCursor? after,
        int limit,
        CancellationToken cancellationToken)
    {
        var (sales, quotations) = Filtered(
            tenantId, clientId, clientIds, advisorId, status, paymentStatus, convertedFrom, convertedTo, saleNumber);

        if (after is not null)
        {
            var convertedAt = after.ConvertedAt;
            var number = after.SaleNumber;
            sales = sales.Where(sale => EF.Functions.LessThan(
                ValueTuple.Create(sale.ConvertedAt, sale.SaleNumber),
                ValueTuple.Create(convertedAt, number)));
        }

        return await (
                from sale in sales
                join quotation in quotations on sale.QuotationId equals quotation.Id
                select new { sale, quotation })
            .OrderByDescending(row => row.sale.ConvertedAt)
            .ThenByDescending(row => row.sale.SaleNumber)
            .Take(limit)
            .Select(row => new SaleWithQuotation(row.sale, row.quotation))
            .ToListAsync(cancellationToken);
    }

    // Los filtros del listado y de su Excel salen de acá y de ningún otro lado. Se aplican sobre
    // cada tabla por separado y el join se arma recién al final, proyectando ahí mismo: filtrar
    // después de proyectar a un tipo propio es lo que hacía que el proveedor no tradujera la
    // consulta y el endpoint respondiera 500. Sin comprobantes ni líneas: la fila no los muestra, y
    // traerlos sería el mismo N+1 que QuotationRepository.SearchAsync evita.
    private (IQueryable<Sale> Sales, IQueryable<Quotation> Quotations) Filtered(
        Guid tenantId,
        Guid? clientId,
        IReadOnlyCollection<Guid>? clientIds,
        MemberId? advisorId,
        SaleStatus? status,
        SalePaymentStatus? paymentStatus,
        DateOnly? convertedFrom,
        DateOnly? convertedTo,
        string? saleNumber)
    {
        var sales = dbContext.Sales
            .AsNoTracking()
            .Where(sale => sale.TenantId == tenantId);

        if (status is { } saleStatus)
        {
            sales = sales.Where(sale => sale.Status == saleStatus);
        }

        if (paymentStatus is { } salePaymentStatus)
        {
            sales = sales.Where(sale => sale.PaymentStatus == salePaymentStatus);
        }

        var saleNumberPattern = LikePattern(saleNumber);
        if (saleNumberPattern is not null)
        {
            sales = sales.Where(sale =>
                EF.Functions.ILike(sale.SaleNumber, saleNumberPattern, LikeEscapeCharacter));
        }

        if (convertedFrom is { } from)
        {
            var fromUtc = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            sales = sales.Where(sale => sale.ConvertedAt >= fromUtc);
        }

        if (convertedTo is { } to)
        {
            // Limite superior exclusivo al dia siguiente, igual que el listado de cotizaciones:
            // "hasta el 30" incluye todo el 30, no solo su instante 00:00:00.
            var toUtcExclusive = new DateTimeOffset(
                to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            sales = sales.Where(sale => sale.ConvertedAt < toUtcExclusive);
        }

        // Cliente y asesora viven en la cotizacion: sus filtros se aplican de ese lado.
        var quotations = dbContext.Quotations
            .AsNoTracking()
            .Where(quotation => quotation.TenantId == tenantId);

        if (clientId is { } client)
        {
            quotations = quotations.Where(quotation => quotation.ClientId == client);
        }

        if (clientIds is not null)
        {
            quotations = quotations.Where(quotation => clientIds.Contains(quotation.ClientId));
        }

        if (advisorId is { } advisor)
        {
            quotations = quotations.Where(quotation => quotation.AdvisorId == advisor);
        }

        return (sales, quotations);
    }

    public void Add(Sale sale) => dbContext.Sales.Add(sale);
}
