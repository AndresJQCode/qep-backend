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
        //
        // Sin comprobantes ni líneas: la fila no las muestra, y traerlas sería el mismo N+1 que
        // QuotationRepository.SearchAsync evita no trayendo lo que la tabla no pinta.
        // Los filtros se aplican sobre cada tabla por separado y el join se arma recien al final,
        // proyectando ahi mismo. Filtrar despues de proyectar a un tipo propio es lo que hacia
        // que el proveedor no tradujera la consulta y el endpoint respondiera 500.
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

        var joined =
            from sale in sales
            join quotation in quotations on sale.QuotationId equals quotation.Id
            select new { sale, quotation };

        var total = await joined.CountAsync(cancellationToken);
        var items = await joined
            // Por fecha de conversión, y el número como desempate: dos ventas del mismo instante
            // --el mismo segundo en una siembra de prueba-- tienen que paginar en un orden
            // estable, o una fila puede repetirse o saltearse entre páginas.
            .OrderByDescending(row => row.sale.ConvertedAt)
            .ThenByDescending(row => row.sale.SaleNumber)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(row => new SaleWithQuotation(row.sale, row.quotation))
            .ToListAsync(cancellationToken);

        return (items, total);
    }

    public void Add(Sale sale) => dbContext.Sales.Add(sale);
}
