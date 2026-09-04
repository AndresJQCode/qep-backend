namespace Modules.Reporting.IntegrationTests;

/// <summary>
/// Las formas que el contrato de API fija, redeclaradas aca y **no** reusadas de
/// <c>Modules.Reporting.Application</c>: deserializar contra el mismo record que serializa el
/// endpoint no verifica nada del contrato — renombrar un campo en los dos lados a la vez dejaria
/// la prueba verde y al frontend roto.
/// </summary>
internal sealed record ReportPageDto<TItem>(
    IReadOnlyList<TItem> Items, int Total, int Page, int PageSize);

internal sealed record SalesReportItem(
    Guid SaleId,
    string SaleNumber,
    Guid QuotationId,
    string QuotationNumber,
    DateTimeOffset ConvertedAt,
    Guid AdvisorId,
    string? AdvisorName,
    Guid ClientId,
    string? ClientName,
    string? ClientCuc,
    string Status,
    string PaymentStatus,
    decimal Subtotal,
    decimal TaxAmount,
    decimal Total);

internal sealed record QuotationsReportItem(
    Guid QuotationId,
    string QuotationNumber,
    DateTimeOffset CreatedAt,
    DateOnly? ValidUntil,
    Guid AdvisorId,
    string? AdvisorName,
    Guid ClientId,
    string? ClientName,
    string? ClientCuc,
    string Status,
    decimal Subtotal,
    decimal TaxAmount,
    decimal Total);

internal sealed record PriceChangeReportItem(
    Guid ChangeId,
    Guid ProductId,
    string ProductCode,
    string ProductName,
    string Field,
    int? ScaleFromUnit,
    int? ScaleToUnit,
    decimal? PreviousValue,
    decimal? NewValue,
    decimal Difference,
    Guid ChangedById,
    string? ChangedByName,
    DateTimeOffset ChangedAt);

internal sealed record CustomerReportItem(
    Guid CustomerId,
    string Cuc,
    string Name,
    string IdentificationType,
    string IdentificationNumber,
    Guid ClassificationId,
    string? ClassificationName,
    Guid? DepartmentId,
    string? DepartmentName,
    Guid CityId,
    string? CityName,
    bool IsActive,
    DateTimeOffset CreatedAt);

/// <summary>El <c>code</c> de un ProblemDetails, que es por lo que el frontend discrimina — no
/// por el texto ni por el status a secas.</summary>
internal sealed record ProblemDto(string? Code, string? Title, int? Status);

/// <summary>El resumen agregado de ventas, tal como el contrato lo fija. Redeclarado igual que
/// el resto — ver la nota del encabezado de este archivo.</summary>
internal sealed record SalesReportSummary(
    int SaleCount,
    decimal Subtotal,
    decimal TaxAmount,
    decimal Total,
    IReadOnlyList<ReportMonthlyPoint> Monthly,
    IReadOnlyList<ReportRankEntry> ByAdvisor,
    IReadOnlyList<ReportRankEntry> ByClient,
    ReportComparison? Previous);

internal sealed record ReportMonthlyPoint(int Year, int Month, int SaleCount, decimal Total);

internal sealed record ReportRankEntry(
    Guid? Id,
    string? Label,
    string? Secondary,
    int EntityCount,
    int SaleCount,
    decimal Total);

internal sealed record ReportComparison(int SaleCount, decimal Total);
