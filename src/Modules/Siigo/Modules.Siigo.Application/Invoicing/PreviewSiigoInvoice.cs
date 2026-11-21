using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Siigo.Application.Invoicing;

/// <summary><c>GET …/orders/{orderId}/siigo-invoice/preview</c> (<c>siigo.invoice.issue</c>).</summary>
public sealed record PreviewSiigoInvoiceQuery(Guid TenantId, Guid OrderId) : IQuery<SiigoInvoicePreviewDto>;

/// <summary>
/// Spec 2026-11-18 §6.3: la factura armada, los faltantes y la propuesta de pagos, <b>sin escribir nada y sin llamar a Siigo salvo la
/// lista de usuarios</b> (cacheada 5 minutos), con la que se elige el vendedor del asesor del pedido (spec 2026-11-20 §4.6): así un asesor
/// sin usuario en Siigo se ve antes de pulsar «Facturar». Nunca busca ni crea el cliente. Lee con las credenciales de configuración
/// (también una conexión pausada: pausar es dejar de facturar, no dejar de mirar). El pedido se valida con las mismas precondiciones de
/// la marca manual, y la moneda, la empresa y la conexión configurada son 422 como al generar; lo que falta del pedido sale como lista,
/// no como error.
/// </summary>
public sealed class PreviewSiigoInvoiceHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ISiigoOrderSource orders,
    ISiigoSettingsRepository settingsRepository,
    ISiigoConnectionDirectory connections,
    ISiigoApi api) : IQueryHandler<PreviewSiigoInvoiceQuery, SiigoInvoicePreviewDto>
{
    public async Task<SiigoInvoicePreviewDto> HandleAsync(PreviewSiigoInvoiceQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await SiigoInvoiceSupport.AuthorizeAsync(executionContext, tenantModules, query.TenantId, cancellationToken);
        var context = await SiigoInvoiceSupport.LoadAsync(orders, settingsRepository, query.TenantId, query.OrderId, cancellationToken);

        var credentials = await SiigoInvoiceSupport.SetupCredentialsAsync(
            connections, query.TenantId, context.Settings.ConnectionId, cancellationToken);
        var sellerId = SiigoSellerResolver.Resolve(context.Input.AdvisorEmail, await api.ListUsersAsync(credentials, cancellationToken));

        return SiigoInvoiceBuilder.Build(context.Input, context.Settings, sellerId, null) switch
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
