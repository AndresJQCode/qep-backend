using System.Text.Json;
using BuildingBlocks.Application;
using Modules.Shopify.Domain;

namespace Modules.Shopify.Application;

/// <summary>El resumen del recorrido: cada fila <c>Imported</c> que se miró cuenta en exactamente una de las otras cuatro.</summary>
public sealed record ShopifyCityBackfillResult(int Scanned, int Reported, int AlreadyReported, int NowMatching, int Skipped);

/// <summary>
/// Spec 2026-10-26 D-C12: el recorrido de los pedidos que ya estaban importados cuando existió la bandeja de ciudades sin resolver.
/// Un pedido importado antes de la bandeja con la ciudad sin reconocer quedó sin parte de envío y con la dirección cruda en las
/// notas, y nadie lo reportó; esto lo reporta, con la misma forma que el importador (<see cref="ShopifyUnresolvedCitySample"/>:
/// sólo la dirección, nunca el nombre ni el teléfono del comprador).
/// <list type="bullet">
/// <item>Una fila <c>Imported</c> con pedido cuenta sólo si el pedido sigue <c>Pending</c>, sin parte de envío, no es de recoger en
/// tienda y sus notas todavía tienen la línea de la dirección cruda; si no, <c>skipped</c>.</item>
/// <item>Se resuelve la ciudad del snapshot con la regla de la importación: si ahora casa (por el nombre real o por un sinónimo)
/// es <c>nowMatching</c> y no se reporta; si no hay departamento (ni Colombia, ni ciudad, ni provincia) es <c>skipped</c>.</item>
/// <item>Si no, se reporta sin cliente: <c>CustomerId</c> nulo y <c>CustomerCityOnly</c> falso (desvío S4, ruling P15). Sólo el
/// cliente que <b>esa</b> importación creó incompleto se completa al confirmar el sinónimo, y el recorrido no puede saber cuál
/// fue: el importador reutiliza clientes incompletos que ya existían (por ejemplo, uno nacido en WhatsApp, también en versión 1) y
/// la única guarda del trabajo es la versión. <c>PartyCityOnly</c> es que al pedido sólo le faltaba la ciudad
/// (<see cref="ShopifyOrderImporter.ShippingPartyFields"/>).</item>
/// </list>
/// Idempotente: la ocurrencia es única por la fila del espejo, así que correrlo dos veces cuenta la segunda vez como
/// <c>alreadyReported</c>. Un error de cualquier fila se propaga y se vuelve a correr entero.
/// </summary>
public sealed class ShopifyUnresolvedCityBackfill(
    IShopifyOrderMirrorRepository mirrors,
    ShopifyCustomerResolver resolver,
    IShopifyCityCorrectionWriter writer,
    IShopifyUnresolvedCityReporter reporter,
    IClock clock)
{
    /// <summary>Filas por lote; el recorrido pagina por id y no carga todo el espejo de una vez.</summary>
    public const int BatchSize = 200;

    // Un solo conteo por fila y por corrida.
    private sealed class Counters
    {
        public int Scanned { get; set; }

        public int Reported { get; set; }

        public int AlreadyReported { get; set; }

        public int NowMatching { get; set; }

        public int Skipped { get; set; }
    }

    public async Task<ShopifyCityBackfillResult> RunAsync(CancellationToken cancellationToken)
    {
        var counters = new Counters();
        Guid? afterId = null;
        while (true)
        {
            var batch = await mirrors.ListImportedAfterAsync(afterId, BatchSize, cancellationToken);
            foreach (var row in batch)
            {
                counters.Scanned++;
                await ProcessAsync(row, counters, cancellationToken);
            }

            if (batch.Count < BatchSize)
            {
                break;
            }

            afterId = batch[^1].Id;
        }

        return new ShopifyCityBackfillResult(
            counters.Scanned, counters.Reported, counters.AlreadyReported, counters.NowMatching, counters.Skipped);
    }

    private async Task ProcessAsync(ShopifyOrderMirror row, Counters counters, CancellationToken cancellationToken)
    {
        // El repositorio sólo trae filas con pedido; el guard es para no confiar en eso.
        if (row.QepOrderId is not { } orderId
            || await writer.ReadOrderCityStateAsync(row.TenantId, orderId, cancellationToken) is not
            {
                Status: "Pending", IsStorePickup: false, HasShippingParty: false, NotesHaveRawAddress: true,
            }
            || ReadSnapshot(row) is not { ShippingAddress: { } address } snapshot)
        {
            counters.Skipped++;
            return;
        }

        var resolution = await resolver.ResolveCityAsync(address, cancellationToken);
        if (resolution.City is not null)
        {
            counters.NowMatching++;
            return;
        }

        if (resolution.UnmatchedDepartmentId is not { } departmentId || resolution.CityText is not { } cityText)
        {
            counters.Skipped++;
            return;
        }

        var result = await reporter.ReportAsync(
            new ShopifyUnresolvedCityReport(
                row.TenantId,
                row.Id,
                orderId,
                CustomerId: null,
                departmentId,
                cityText,
                ShopifyUnresolvedCitySample.Of(address),
                Blank(address.ProvinceCode),
                PartyCityOnly: ShopifyOrderImporter.ShippingPartyFields(snapshot) is not null,
                CustomerCityOnly: false,
                row.ImportedAt ?? clock.UtcNow),
            cancellationToken);
        switch (result.Outcome)
        {
            case ShopifyUnresolvedCityOutcome.Recorded:
                counters.Reported++;
                break;
            case ShopifyUnresolvedCityOutcome.AlreadyRecorded:
                counters.AlreadyReported++;
                break;
            case ShopifyUnresolvedCityOutcome.SynonymExists:
                // Se confirmó un sinónimo entre resolver la ciudad y reportar: ahora casa. No se reporta ni se siembra nada.
                counters.NowMatching++;
                break;
            default:
                // Ignored: un texto que se normaliza a vacío no tiene nada que resolver.
                counters.Skipped++;
                break;
        }
    }

    // Un payload que no se lee no se arregla recorriendo otra vez: esa fila se salta.
    private static ShopifyOrderSnapshot? ReadSnapshot(ShopifyOrderMirror row)
    {
        try
        {
            return ShopifyOrderIngestion.ReadPayload(row.Payload);
        }
        catch (Exception exception) when (exception is JsonException or ShopifyPayloadException)
        {
            return null;
        }
    }

    private static string? Blank(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
