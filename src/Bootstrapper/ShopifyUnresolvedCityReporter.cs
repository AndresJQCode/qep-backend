using Modules.Geography.Application;
using Modules.Geography.Domain;
using Modules.Shopify.Application;

namespace Bootstrapper;

/// <summary>Spec 2026-10-26 D-C3: Shopify → la bandeja de Geography. La ocurrencia lleva el origen <c>shopify</c> y la fila del
/// espejo como referencia, que la hace idempotente: reimportar o conciliar el mismo pedido no la duplica. Geography confirma en
/// su propia transacción; lo que lance se propaga y la entrega se reintenta.</summary>
internal sealed class ShopifyUnresolvedCityReporter(IUnresolvedCityReporter reporter) : IShopifyUnresolvedCityReporter
{
    public const string Source = "shopify";

    public async Task<ShopifyUnresolvedCityResult> ReportAsync(
        ShopifyUnresolvedCityReport report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        var result = await reporter.ReportAsync(
            new UnresolvedCityReport(
                report.TenantId,
                Source,
                report.MirrorOrderId,
                report.OrderId,
                report.CustomerId,
                new DepartmentId(report.DepartmentId),
                report.CityText,
                report.SampleAddress,
                report.ProvinceCode,
                report.PartyCityOnly,
                report.CustomerCityOnly,
                report.SeenAt),
            cancellationToken);
        return new ShopifyUnresolvedCityResult(Outcome(result.Outcome), result.SynonymId?.Value);
    }

    private static ShopifyUnresolvedCityOutcome Outcome(UnresolvedCityReportOutcome outcome) => outcome switch
    {
        UnresolvedCityReportOutcome.Recorded => ShopifyUnresolvedCityOutcome.Recorded,
        UnresolvedCityReportOutcome.AlreadyRecorded => ShopifyUnresolvedCityOutcome.AlreadyRecorded,
        UnresolvedCityReportOutcome.SynonymExists => ShopifyUnresolvedCityOutcome.SynonymExists,
        UnresolvedCityReportOutcome.Ignored => ShopifyUnresolvedCityOutcome.Ignored,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unknown unresolved-city outcome."),
    };
}
