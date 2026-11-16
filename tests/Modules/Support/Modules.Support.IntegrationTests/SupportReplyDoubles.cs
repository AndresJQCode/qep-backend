using System.Collections.Concurrent;
using Modules.Support.Application;
using Modules.Support.Domain;

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

    /// <summary>Fase 4b: las conexiones con la plantilla «Respuesta formal» aprobada, aparte de las de «Aviso de respuesta».</summary>
    public ConcurrentDictionary<Guid, string> ApprovedFormalConnections { get; } = new();

    public ConcurrentQueue<SupportChannelThreadSend> ThreadSends { get; } = new();

    public ConcurrentQueue<SupportChannelTemplateSend> TemplateSends { get; } = new();

    public ConcurrentQueue<SupportChannelThreadDocumentSend> DocumentSends { get; } = new();

    public ConcurrentQueue<SupportChannelFormalTemplateSend> FormalTemplateSends { get; } = new();

    /// <summary>Lo que devuelve el siguiente envío por el hilo; por defecto, enviado con un mensaje nuevo.</summary>
    public Func<SupportChannelThreadSend, SupportChannelSendResult> OnThread { get; set; } =
        send => new SupportChannelSendResult.Sent(Guid.CreateVersion7(), send.ConversationId);

    /// <summary>Lo que devuelve el siguiente envío de la plantilla; por defecto, enviado a la conversación de la llamada.</summary>
    public Func<SupportChannelTemplateSend, SupportChannelSendResult> OnTemplate { get; set; } =
        send => new SupportChannelSendResult.Sent(Guid.CreateVersion7(), send.ConversationId ?? Guid.CreateVersion7());

    /// <summary>Lo que devuelve el siguiente documento por el hilo; por defecto, un solo mensaje que lleva el PDF (WhatsApp).</summary>
    public Func<SupportChannelThreadDocumentSend, SupportChannelSendResult> OnDocument { get; set; } = send =>
    {
        var message = Guid.CreateVersion7();
        return new SupportChannelSendResult.Sent(message, send.ConversationId, message);
    };

    /// <summary>Lo que devuelve la siguiente plantilla formal; por defecto, enviada a la conversación de la llamada.</summary>
    public Func<SupportChannelFormalTemplateSend, SupportChannelSendResult> OnFormalTemplate { get; set; } =
        send => new SupportChannelSendResult.Sent(Guid.CreateVersion7(), send.ConversationId ?? Guid.CreateVersion7());

    public Task<SupportThreadStatus?> FindThreadAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken) =>
        Task.FromResult(Threads.TryGetValue(conversationId, out var thread) ? thread : null);

    public Task<IReadOnlyList<SupportConnectionOption>> ListTemplateConnectionsAsync(Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SupportConnectionOption>>(Options(ApprovedConnections));

    public Task<bool> IsTemplateApprovedAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        Task.FromResult(ApprovedConnections.ContainsKey(connectionId));

    public Task<IReadOnlyList<SupportConnectionOption>> ListFormalTemplateConnectionsAsync(
        Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SupportConnectionOption>>(Options(ApprovedFormalConnections));

    public Task<bool> IsFormalTemplateApprovedAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        Task.FromResult(ApprovedFormalConnections.ContainsKey(connectionId));

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

    public Task<SupportChannelSendResult> SendThreadDocumentAsync(
        SupportChannelThreadDocumentSend send, CancellationToken cancellationToken)
    {
        DocumentSends.Enqueue(send);
        return Task.FromResult(OnDocument(send));
    }

    public Task<SupportChannelSendResult> SendFormalTemplateAsync(
        SupportChannelFormalTemplateSend send, CancellationToken cancellationToken)
    {
        FormalTemplateSends.Enqueue(send);
        return Task.FromResult(OnFormalTemplate(send));
    }

    /// <summary>Fase 4b: las conexiones con la plantilla de la encuesta aprobada.</summary>
    public ConcurrentDictionary<Guid, string> ApprovedSurveyConnections { get; } = new();

    public ConcurrentQueue<SupportChannelSurveyTemplateSend> SurveySends { get; } = new();

    public Func<SupportChannelSurveyTemplateSend, SupportChannelSendResult> OnSurvey { get; set; } =
        send => new SupportChannelSendResult.Sent(Guid.CreateVersion7(), send.ConversationId);

    public Task<bool> IsSurveyTemplateApprovedAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        Task.FromResult(ApprovedSurveyConnections.ContainsKey(connectionId));

    public Task<SupportChannelSendResult> SendSurveyTemplateAsync(
        SupportChannelSurveyTemplateSend send, CancellationToken cancellationToken)
    {
        SurveySends.Enqueue(send);
        return Task.FromResult(OnSurvey(send));
    }

    private static List<SupportConnectionOption> Options(ConcurrentDictionary<Guid, string> connections) =>
        connections.Select(pair => new SupportConnectionOption(pair.Key, pair.Value)).OrderBy(option => option.Name).ToList();
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

/// <summary>Fase 4b: el correo de la encuesta, que anota lo que manda en vez de mandarlo.</summary>
internal sealed class RecordingSupportSurveyEmail : ISupportSurveyEmail
{
    public ConcurrentQueue<SupportSurveyEmail> Sent { get; } = new();

    /// <summary>Corre antes de anotar el envío: deja que la prueba mire la base en el momento exacto del envío.</summary>
    public Func<SupportSurveyEmail, Task>? OnSend { get; set; }

    /// <summary>Con valor, el envío lanza esta excepción en vez de anotar: una falla inesperada del canal.</summary>
    public Exception? Throw { get; set; }

    public async Task<SupportChannelSendResult> SendAsync(SupportSurveyEmail email, CancellationToken cancellationToken)
    {
        if (OnSend is { } onSend)
        {
            await onSend(email);
        }

        if (Throw is { } exception)
        {
            throw exception;
        }

        Sent.Enqueue(email);
        return new SupportChannelSendResult.Sent(null, null);
    }
}

/// <summary>Fase 4b: el generador del PDF de la respuesta formal sin <c>qcode-pdf</c>. Anota cada documento que le piden y devuelve
/// <see cref="Content"/>; con <see cref="Fail"/> lanza lo mismo que el generador real cuando el servicio no responde el PDF.</summary>
internal sealed class RecordingSupportFormalReplyRenderer : ISupportFormalReplyRenderer
{
    public static byte[] Content => "%PDF-1.7\n1 0 obj\n<<>>\nendobj\ntrailer\n<<>>\n%%EOF\n"u8.ToArray();

    public ConcurrentQueue<SupportFormalReplyDocument> Rendered { get; } = new();

    public bool Fail { get; set; }

    public Task<byte[]> RenderAsync(SupportFormalReplyDocument document, CancellationToken cancellationToken)
    {
        Rendered.Enqueue(document);
        return Fail
            ? throw new SupportDomainException(SupportErrorCodes.ReplyDocumentRenderFailed, "qcode-pdf answered 502.")
            : Task.FromResult(Content);
    }
}

/// <summary>Fase 4b: el bucket privado de los PDF en memoria. Anota lo guardado y lo borrado: un documento que no llegó a guardarse
/// tiene que salir del bucket (H7), y el de una respuesta rechazada antes de enviar también.</summary>
internal sealed class RecordingSupportDocumentStorage : ISupportDocumentStorage
{
    public ConcurrentDictionary<string, byte[]> Objects { get; } = new();

    public ConcurrentQueue<string> Deleted { get; } = new();

    public ConcurrentQueue<string> Reads { get; } = new();

    public Task<string> SaveAsync(Guid tenantId, byte[] content, CancellationToken cancellationToken)
    {
        var key = $"support/tenants/{tenantId:N}/replies/2026/11/{Guid.NewGuid():N}.pdf";
        Objects[key] = content.ToArray();
        return Task.FromResult(key);
    }

    public Task<byte[]> ReadAsync(string storageKey, CancellationToken cancellationToken)
    {
        Reads.Enqueue(storageKey);
        return Task.FromResult(Objects[storageKey].ToArray());
    }

    public Task<Uri> CreateDownloadUrlAsync(string storageKey, string fileName, TimeSpan lifetime, CancellationToken cancellationToken) =>
        Task.FromResult(new Uri(
            $"https://r2.test/{storageKey}?filename={Uri.EscapeDataString(fileName)}&seconds={(int)lifetime.TotalSeconds}"));

    public Task DeleteAsync(string storageKey, CancellationToken cancellationToken)
    {
        Deleted.Enqueue(storageKey);
        Objects.TryRemove(storageKey, out _);
        return Task.CompletedTask;
    }
}

/// <summary>Fase 4b: el membrete sin Companies ni el formulario. <see cref="Data"/> en null es un tenant sin empresa que responda
/// (<c>organization_required</c>); el adaptador real lo amarran <c>SupportLetterheadTests</c>.</summary>
internal sealed class FakeSupportLetterhead : ISupportLetterhead
{
    public SupportLetterheadData? Data { get; set; } = new(
        "Lulo Servicios S.A.S.", "NIT", "900123456-7", "Calle 10 # 20-30", "Medellín", "soporte@lulo.co", "6045551234", null);

    public Task<SupportLetterheadData?> FindAsync(Guid tenantId, Guid? formVersionId, CancellationToken cancellationToken) =>
        Task.FromResult(Data);

    public Task<bool> ExistsAsync(Guid tenantId, Guid? formVersionId, CancellationToken cancellationToken) =>
        Task.FromResult(Data is not null);
}

/// <summary>Revisión del bloque C: arma <see cref="ThrowAfterCommitUnitOfWork"/> para el siguiente guardado.</summary>
internal sealed class ThrowAfterCommitSwitch
{
    public bool Armed { get; set; }
}

/// <summary>Revisión del bloque C: el unit of work real, salvo que armado lanza después de que el guardado confirmó, una sola vez. Es
/// lo que ve el handler cuando la conexión se cae al volver del commit: la fila quedó guardada y el request termina en error.</summary>
internal sealed class ThrowAfterCommitUnitOfWork(ISupportUnitOfWork inner, ThrowAfterCommitSwitch commitSwitch) : ISupportUnitOfWork
{
    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        await inner.SaveChangesAsync(cancellationToken);
        if (commitSwitch.Armed)
        {
            commitSwitch.Armed = false;
            throw new IOException("The connection dropped after the commit.");
        }
    }

    public Task<ISupportTransaction> BeginTransactionAsync(CancellationToken cancellationToken) =>
        inner.BeginTransactionAsync(cancellationToken);

    public Task HoldCustomerReferenceAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken) =>
        inner.HoldCustomerReferenceAsync(tenantId, customerId, cancellationToken);

    public void DiscardChanges() => inner.DiscardChanges();
}
