using Modules.Quotations.Application;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// Los canales de WhatsApp del tenant en una prueba (plan 2026-10-21, Zenvia por tenant). Ninguna prueba llama a Zenvia
/// ni a Meta (spec 2026-10-21 D18), así que el host sustituye el adaptador real por este, con un canal por defecto. Una
/// prueba que quiere cero o varios canales cambia <see cref="Channels"/> antes de enviar.
/// </summary>
public sealed class RecordingWhatsAppChannelDirectory : IWhatsAppChannelDirectory
{
    public static readonly WhatsAppChannel Default = new(
        Guid.Parse("01900000-0000-7000-8000-00000000c0de"), "token-de-prueba", "570000000000",
        "d0e79149-ed61-4784-9883-d86807f46ce5", "0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d");

    public List<WhatsAppChannel> Channels { get; } = [Default];

    public Task<IReadOnlyList<WhatsAppChannel>> ListAvailableAsync(Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<WhatsAppChannel>>([.. Channels]);
}

/// <summary>Lo que el envío le entregó a Zenvia, sin salir a la red: el canal y el número del documento.</summary>
public sealed class RecordingWhatsAppSender : IWhatsAppSender
{
    private readonly Lock gate = new();
    private readonly List<(WhatsAppChannel Channel, string DocumentNumber)> sent = [];
    private string? lastNumberFormat;

    /// <summary>El formato numérico del tenant con el que se armó el último mensaje (spec 2026-10-23 D-R3).</summary>
    public string? LastNumberFormat
    {
        get
        {
            lock (gate)
            {
                return lastNumberFormat;
            }
        }
    }

    public IReadOnlyList<(WhatsAppChannel Channel, string DocumentNumber)> Sent
    {
        get
        {
            lock (gate)
            {
                return [.. sent];
            }
        }
    }

    public Task SendQuotationAsync(
        WhatsAppChannel channel, WhatsAppQuotationMessage message, CancellationToken cancellationToken) =>
        Record(channel, message.OrderNumber, message.NumberFormat);

    public Task SendOrderAsync(
        WhatsAppChannel channel, WhatsAppOrderMessage message, CancellationToken cancellationToken) =>
        Record(channel, message.OrderNumber, message.NumberFormat);

    private Task Record(WhatsAppChannel channel, string documentNumber, string? numberFormat)
    {
        lock (gate)
        {
            sent.Add((channel, documentNumber));
            lastNumberFormat = numberFormat;
        }

        return Task.CompletedTask;
    }
}
