using System.Diagnostics;

namespace BuildingBlocks.Application;

/// <summary>
/// Las colas que tienen timbre (spec 2026-11-13, D-7) y la red de seguridad de cada una: lo más que un worker espera sin
/// aviso cuando los timbres están prendidos. El nombre es el sufijo del canal <c>qep:wake:{cola}</c>.
/// </summary>
public static class DoorbellQueues
{
    public const string Automation = "automation";
    public const string Webhook = "webhook";
    public const string ShopifyWebhook = "shopify_webhook";
    public const string Media = "media";
    public const string Scheduled = "scheduled";
    public const string Campaign = "campaign";
    public const string Export = "export";
    public const string Outbox = "outbox";

    /// <summary>Soporte fase 4b (H15): las encuestas de satisfacción que pide cada resolución.</summary>
    public const string SupportSurvey = "support_survey";

    /// <summary>Spec 2026-11-17 §5.2: las importaciones de productos de Shopify.</summary>
    public const string ShopifyProducts = "shopify_products";

    /// <summary>Las tandas de facturación con Siigo: un <c>INSERT</c> en <c>siigo.invoice_batch_items</c> timbra.</summary>
    public const string SiigoInvoice = "siigo_invoice";

    public static IReadOnlyList<string> All { get; } =
        [Automation, Webhook, ShopifyWebhook, Media, Scheduled, Campaign, Export, Outbox, SupportSurvey, ShopifyProducts, SiigoInvoice];

    public static TimeSpan SafetyNet(string queue) => queue switch
    {
        Automation => TimeSpan.FromSeconds(15),
        Webhook => TimeSpan.FromSeconds(15),
        ShopifyWebhook => TimeSpan.FromSeconds(30),
        Media => TimeSpan.FromSeconds(30),
        Scheduled => TimeSpan.FromSeconds(5),
        Campaign => TimeSpan.FromSeconds(30),
        Export => TimeSpan.FromSeconds(30),
        Outbox => TimeSpan.FromSeconds(30),
        SupportSurvey => TimeSpan.FromSeconds(30),
        ShopifyProducts => TimeSpan.FromSeconds(30),
        SiigoInvoice => TimeSpan.FromSeconds(30),
        _ => throw new ArgumentOutOfRangeException(nameof(queue), queue, "Unknown doorbell queue."),
    };
}

/// <summary>
/// El timbre de las colas (spec 2026-11-13, D-7; plan P8). Quien escribe en una cola timbra después del commit; el worker de
/// esa cola espera con un <see cref="IDoorbellListener"/> y vuelve al primer aviso. El timbre es una optimización: la red de
/// seguridad, o el intervalo de hoy sin timbres, cubre todo aviso perdido.
/// </summary>
public interface IQueueDoorbell
{
    /// <summary>Avisa que la cola tiene trabajo. No espera la respuesta y nunca lanza.</summary>
    void Ring(string queue);

    /// <summary>Un oyente por bucle de worker; un aviso despierta a todos los oyentes vivos de la cola.</summary>
    IDoorbellListener Listen(string queue);
}

public interface IDoorbellListener : IDisposable
{
    /// <summary>
    /// Conectado y área prendida: vuelve con <c>true</c> al primer aviso o con <c>false</c> al pasar
    /// <paramref name="safetyNet"/>. Si no: espera <paramref name="poll"/> (el intervalo de hoy) y vuelve con <c>false</c>. Un
    /// aviso que llegó mientras no se esperaba hace volver al instante.
    /// </summary>
    Task<bool> WaitAsync(TimeSpan poll, TimeSpan safetyNet, CancellationToken cancellationToken);

    /// <summary>
    /// Área prendida, conectado y suscrito: los avisos llegan. Sólo entonces el bucle repite un lote lleno sin esperar; si no, espera el
    /// intervalo de hoy aunque haya más (Global Constraints: área apagada o Valkey caído = el intervalo de hoy).
    /// </summary>
    bool IsLive { get; }
}

public static class DoorbellPolling
{
    /// <summary>
    /// El bucle de un worker con timbre: corre <paramref name="tick"/>; mientras devuelva <c>true</c> (el lote vino lleno, puede
    /// haber más) y el oyente esté vivo, lo repite sin esperar; si no, espera con el oyente. Sale sin lanzar cuando se cancela
    /// <paramref name="stoppingToken"/>. El tick maneja sus propios errores: una excepción que no sea el apagado sube.
    /// </summary>
    public static Task RunAsync(
        IDoorbellListener listener,
        TimeSpan poll,
        TimeSpan safetyNet,
        Func<CancellationToken, Task<bool>> tick,
        CancellationToken stoppingToken)
    {
        ArgumentNullException.ThrowIfNull(tick);
        return RunAsync(
            listener,
            poll,
            safetyNet,
            async cancellationToken => await tick(cancellationToken).ConfigureAwait(false) ? TimeSpan.Zero : (TimeSpan?)null,
            stoppingToken);
    }

    /// <summary>
    /// El bucle con un próximo vencimiento conocido (plan P11): el tick devuelve <c>null</c> si no conoce ninguno, <c>TimeSpan.Zero</c>
    /// si hay más trabajo ya, o cuánto falta para el próximo. La espera con el oyente usa <c>min(safetyNet, vencimiento)</c> como red; el
    /// intervalo de hoy (<paramref name="poll"/>, el camino sin timbres) no cambia por el vencimiento.
    ///
    /// Al <paramref name="poll"/> se le descuenta lo que tardó el tick (nunca menos de cero): sin timbres, la cadencia es la del
    /// <c>PeriodicTimer</c> de antes, que contaba desde el comienzo del tick anterior y no desde su final.
    ///
    /// Un vencimiento cero (hay más ya) repite sin esperar sólo con el oyente vivo (<see cref="IDoorbellListener.IsLive"/>). Con el área
    /// apagada, Valkey caído o sin suscripción, espera el intervalo de hoy menos lo que tardó el tick, igual que antes: un worker que
    /// marca fallidos sin reintento (los correos) no quema su cola en segundos durante una caída del proveedor.
    /// </summary>
    public static async Task RunAsync(
        IDoorbellListener listener,
        TimeSpan poll,
        TimeSpan safetyNet,
        Func<CancellationToken, Task<TimeSpan?>> tick,
        CancellationToken stoppingToken)
    {
        ArgumentNullException.ThrowIfNull(listener);
        ArgumentNullException.ThrowIfNull(tick);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var started = Stopwatch.GetTimestamp();
                var due = await tick(stoppingToken).ConfigureAwait(false);
                if (due is { } next && next <= TimeSpan.Zero && listener.IsLive)
                {
                    continue;
                }

                var remainingPoll = poll - Stopwatch.GetElapsedTime(started);
                var net = due is { } known && known < safetyNet ? known : safetyNet;
                await listener
                    .WaitAsync(remainingPoll > TimeSpan.Zero ? remainingPoll : TimeSpan.Zero, net, stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }
}
