using BuildingBlocks.Application;
using Microsoft.Extensions.Logging;
using Modules.Tenancy.Application;

namespace Modules.Siigo.Application.Invoicing;

/// <summary><c>GET …/orders/{orderId}/siigo-invoice/preview</c> (<c>siigo.invoice.issue</c>).</summary>
public sealed record PreviewSiigoInvoiceQuery(Guid TenantId, Guid OrderId) : IQuery<SiigoInvoicePreviewDto>;

/// <summary>
/// Spec 2026-11-18 §6.3: la factura armada, los faltantes y la propuesta de pagos, <b>sin llamar a Siigo</b> (ni siquiera para buscar o
/// crear el cliente; el vendedor sale del vínculo del asesor, spec 2026-11-20). El pedido se valida con las mismas precondiciones de la
/// marca manual, y la moneda, la empresa y la conexión configurada son 422 como al generar; lo que falta del pedido sale como lista, no
/// como error.
/// <para><b>Escribe una sola cosa</b> (spec 2026-11-26): si la ciudad del exterior del pedido no se pudo vincular, la anota en la bandeja
/// del operador, en Geography (<see cref="SiigoForeignCityAnnotation"/>). Es idempotente por pedido (verla muchas veces deja una sola
/// ocurrencia) y una falla no tumba la respuesta. La disponibilidad no hace esto.</para>
/// </summary>
public sealed class PreviewSiigoInvoiceHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ISiigoOrderSource orders,
    ISiigoSettingsRepository settingsRepository,
    ISiigoForeignCityInbox foreignCities,
    ILogger<PreviewSiigoInvoiceHandler> logger) : IQueryHandler<PreviewSiigoInvoiceQuery, SiigoInvoicePreviewDto>
{
    public async Task<SiigoInvoicePreviewDto> HandleAsync(PreviewSiigoInvoiceQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await SiigoInvoiceSupport.AuthorizeAsync(executionContext, tenantModules, query.TenantId, cancellationToken);
        var context = await SiigoInvoiceSupport.LoadAsync(orders, settingsRepository, query.TenantId, query.OrderId, cancellationToken);
        await SiigoForeignCityAnnotation.AnnotateAsync(foreignCities, logger, query.TenantId, context.Input, cancellationToken);

        return SiigoInvoiceBuilder.Build(context.Input, context.Settings, null) switch
        {
            SiigoBuildResult.Ready ready => new SiigoInvoicePreviewDto(
                [
                    .. ready.Drafts.Select(draft => new SiigoInvoiceDraftDto(
                        draft.Kind.ToString(),
                        draft.Total,
                        [.. (draft.Payments ?? []).Select(payment => new SiigoDraftPaymentDto(
                            payment.ProofId, payment.SiigoPaymentTypeId, payment.Value, payment.DueDate))])),
                ],
                [],
                null),
            SiigoBuildResult.NotReady notReady => new SiigoInvoicePreviewDto(
                [], [.. notReady.Missing.Select(value => new SiigoMissingDto(value.Code, value.Reference, value.Label))], null),
            SiigoBuildResult.TotalsMismatch mismatch => new SiigoInvoicePreviewDto(
                [], [], new SiigoTotalsMismatchDto(mismatch.QepTotal, mismatch.ComputedTotal)),
            _ => throw new InvalidOperationException("Unknown build result."),
        };
    }
}
