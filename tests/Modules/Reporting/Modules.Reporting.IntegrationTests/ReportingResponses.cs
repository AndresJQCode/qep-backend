namespace Modules.Reporting.IntegrationTests;

/// <summary>
/// Las formas que el contrato de API fija, redeclaradas aca y **no** reusadas de
/// <c>Modules.Reporting.Application</c>: deserializar contra el mismo record que serializa el
/// endpoint no verifica nada del contrato — renombrar un campo en los dos lados a la vez dejaria
/// la prueba verde y al frontend roto.
/// </summary>
internal sealed record ReportPageDto<TItem>(
    IReadOnlyList<TItem> Items, int Total, int Page, int PageSize);

internal sealed record OrdersReportItem(
    Guid OrderId,
    string OrderNumber,
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
    string Currency,
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
    string Currency,
    decimal Subtotal,
    decimal TaxAmount,
    decimal Total);

internal sealed record PriceChangeReportItem(
    Guid ChangeId,
    Guid ProductId,
    string ProductCode,
    string ProductName,
    string Field,
    string? Currency,
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

/// <summary>El resumen agregado de pedidos, tal como el contrato lo fija. Redeclarado igual que
/// el resto — ver la nota del encabezado de este archivo.</summary>
internal sealed record OrdersReportSummary(
    int OrderCount,
    IReadOnlyList<MoneyAmount> Subtotals,
    IReadOnlyList<MoneyAmount> TaxAmounts,
    IReadOnlyList<MoneyAmount> Totals,
    IReadOnlyList<ReportMonthlyPoint> Monthly,
    IReadOnlyList<ReportRankEntry> ByAdvisor,
    IReadOnlyList<ReportRankEntry> ByClient,
    ReportComparison? Previous);

/// <summary>One amount in one currency: every money figure of the orders and quotations reports is
/// a list of these. Not named <c>ReportMoney</c>: that is the Application helper class.</summary>
internal sealed record MoneyAmount(string Currency, decimal Amount);

internal sealed record ReportMonthlyPoint(int Year, int Month, int Count, IReadOnlyList<MoneyAmount> Totals);

internal sealed record ReportRankEntry(
    Guid? Id,
    string? Label,
    string? Secondary,
    int EntityCount,
    int Count,
    IReadOnlyList<MoneyAmount> Totals);

internal sealed record ReportComparison(int Count, IReadOnlyList<MoneyAmount> Totals);

/// <summary>El resumen agregado de cotizaciones, tal como el contrato lo fija. Redeclarado igual
/// que el resto — ver la nota del encabezado.</summary>
internal sealed record QuotationsReportSummary(
    int QuotationCount,
    IReadOnlyList<MoneyAmount> Subtotals,
    IReadOnlyList<MoneyAmount> TaxAmounts,
    IReadOnlyList<MoneyAmount> Totals,
    IReadOnlyList<ReportMonthlyPoint> Monthly,
    IReadOnlyList<ReportStatusSlice> ByStatus,
    IReadOnlyList<ReportRankEntry> ByAdvisor,
    QuotationValidity Validity,
    IReadOnlyList<QuotationExpiring> Expiring,
    ReportComparison? Previous);

internal sealed record ReportStatusSlice(string Status, int Count, IReadOnlyList<MoneyAmount> Totals);

internal sealed record QuotationValidity(
    ReportBucket Expired,
    ReportBucket WithinSevenDays,
    ReportBucket WithinThirtyDays,
    ReportBucket Beyond,
    int WithoutExpiry);

internal sealed record ReportBucket(int Count, IReadOnlyList<MoneyAmount> Totals);

internal sealed record QuotationExpiring(
    Guid QuotationId,
    string QuotationNumber,
    DateOnly ValidUntil,
    int DaysLeft,
    string? ClientName,
    string? ClientCuc,
    string? AdvisorName,
    string Currency,
    decimal Total);

/// <summary>
/// El resumen agregado de cambios de precio, tal como el contrato lo fija. Redeclarado igual que
/// el resto — ver la nota del encabezado.
///
/// **Sin ningun agregado de monto**: los valores del historico conviven en dolares, pesos y puntos
/// de descuento, asi que lo unico que se puede sumar sin mentir son filas.
/// </summary>
internal sealed record PriceChangeReportSummary(
    int ChangeCount,
    int ProductCount,
    int IncreaseCount,
    int DecreaseCount,
    IReadOnlyList<ReportCountPoint> Monthly,
    IReadOnlyList<PriceChangeFieldSlice> ByField,
    IReadOnlyList<PriceChangeProductEntry> ByProduct,
    PriceChangeComparison? Previous);

internal sealed record ReportCountPoint(int Year, int Month, int Count);

internal sealed record PriceChangeFieldSlice(string Field, string? Currency, int Count);

internal sealed record PriceChangeProductEntry(
    Guid? ProductId,
    string? ProductName,
    string? ProductCode,
    int EntityCount,
    int Count);

internal sealed record PriceChangeComparison(int ChangeCount);

/// <summary>
/// El resumen del reporte de clientes.
///
/// **Sin ningun agregado de monto tampoco, y por otro motivo**: un cliente no factura ni vence, no
/// tiene un numero que sumar. Lo que se cuenta son clientes.
///
/// Los inactivos no viajan: son <c>CustomerCount - ActiveCount</c>.
/// </summary>
internal sealed record CustomerReportSummary(
    int CustomerCount,
    int ActiveCount,
    IReadOnlyList<ReportCountPoint> Monthly,
    IReadOnlyList<CustomerGroupEntry> ByClassification,
    IReadOnlyList<CustomerGroupEntry> ByDepartment,
    CustomerComparison? Previous);

internal sealed record CustomerGroupEntry(
    Guid? Id,
    string? Label,
    int EntityCount,
    int Count);

internal sealed record CustomerComparison(int CustomerCount);
