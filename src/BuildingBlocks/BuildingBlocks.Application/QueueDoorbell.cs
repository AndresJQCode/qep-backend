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

    public static IReadOnlyList<string> All { get; } =
        [Automation, Webhook, ShopifyWebhook, Media, Scheduled, Campaign, Export, Outbox];

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
}

public static class DoorbellPolling
{
    /// <summary>
    /// El bucle de un worker con timbre: corre <paramref name="tick"/>; mientras devuelva <c>true</c> (el lote vino lleno, puede
    /// haber más) lo repite sin esperar; si no, espera con el oyente. Sale sin lanzar cuando se cancela
    /// <paramref name="stoppingToken"/>. El tick maneja sus propios errores: una excepción que no sea el apagado sube.
    /// </summary>
    public static async Task RunAsync(
        IDoorbellListener listener,
        TimeSpan poll,
        TimeSpan safetyNet,
        Func<CancellationToken, Task<bool>> tick,
        CancellationToken stoppingToken)
    {
        ArgumentNullException.ThrowIfNull(listener);
        ArgumentNullException.ThrowIfNull(tick);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await tick(stoppingToken).ConfigureAwait(false))
                {
                    continue;
                }

                await listener.WaitAsync(poll, safetyNet, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }
}
