using System.Collections.Concurrent;
using Modules.Support.Application;

namespace Modules.Support.IntegrationTests;

/// <summary>
/// El doble de <see cref="ISupportReplyChannels"/> para las pruebas de respuestas: el estado de cada conversación, las conexiones con la
/// plantilla aprobada y lo que contesta cada envío los fija la prueba; anota cada llamada. Reemplaza el puerto de Soporte y no a Meta: lo
/// que hace Mensajería con un envío ya lo amarran <c>SupportReplyOutboundTests</c>; acá se prueba lo que decide Soporte.
/// </summary>
internal sealed class FakeSupportReplyChannels : ISupportReplyChannels
{
    public ConcurrentDictionary<Guid, SupportThreadStatus> Threads { get; } = new();

    public ConcurrentDictionary<Guid, string> ApprovedConnections { get; } = new();

    public ConcurrentQueue<SupportChannelThreadSend> ThreadSends { get; } = new();

    public ConcurrentQueue<SupportChannelTemplateSend> TemplateSends { get; } = new();

    /// <summary>Lo que devuelve el siguiente envío por el hilo; por defecto, enviado con un mensaje nuevo.</summary>
    public Func<SupportChannelThreadSend, SupportChannelSendResult> OnThread { get; set; } =
        send => new SupportChannelSendResult.Sent(Guid.CreateVersion7(), send.ConversationId);

    /// <summary>Lo que devuelve el siguiente envío de la plantilla; por defecto, enviado a la conversación de la llamada.</summary>
    public Func<SupportChannelTemplateSend, SupportChannelSendResult> OnTemplate { get; set; } =
        send => new SupportChannelSendResult.Sent(Guid.CreateVersion7(), send.ConversationId ?? Guid.CreateVersion7());

    public Task<SupportThreadStatus?> FindThreadAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken) =>
        Task.FromResult(Threads.TryGetValue(conversationId, out var thread) ? thread : null);

    public Task<IReadOnlyList<SupportConnectionOption>> ListTemplateConnectionsAsync(Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SupportConnectionOption>>(
            ApprovedConnections.Select(pair => new SupportConnectionOption(pair.Key, pair.Value)).OrderBy(option => option.Name).ToList());

    public Task<bool> IsTemplateApprovedAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        Task.FromResult(ApprovedConnections.ContainsKey(connectionId));

    public Task<SupportChannelSendResult> SendThreadAsync(SupportChannelThreadSend send, CancellationToken cancellationToken)
    {
        ThreadSends.Enqueue(send);
        return Task.FromResult(OnThread(send));
    }

    public Task<SupportChannelSendResult> SendTemplateAsync(SupportChannelTemplateSend send, CancellationToken cancellationToken)
    {
        TemplateSends.Enqueue(send);
        return Task.FromResult(OnTemplate(send));
    }
}

/// <summary>El correo de Soporte que anota lo que manda en vez de mandarlo; <see cref="Fail"/> lo hace fallar como el canal real
/// (<c>send_failed</c>).</summary>
internal sealed class RecordingSupportReplyEmail : ISupportReplyEmail
{
    public ConcurrentQueue<SupportReplyEmail> Sent { get; } = new();

    public bool Fail { get; set; }

    public Task<SupportChannelSendResult> SendAsync(SupportReplyEmail email, CancellationToken cancellationToken)
    {
        Sent.Enqueue(email);
        return Task.FromResult<SupportChannelSendResult>(Fail
            ? new SupportChannelSendResult.Failed(null, SupportChannelReasons.SendFailed)
            : new SupportChannelSendResult.Sent(null, null));
    }
}
