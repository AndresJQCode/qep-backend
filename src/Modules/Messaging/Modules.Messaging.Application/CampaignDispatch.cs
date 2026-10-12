using System.Text.RegularExpressions;
using BuildingBlocks.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.Application;

/// <summary>Spec 2026-10-11 §6.3 paso 2: una campaña <c>Sending</c> a la que todavía no se le escribió la audiencia.</summary>
public sealed record CampaignFreezeTarget(
    Guid CampaignId, Guid TenantId, string TemplateCategory, IReadOnlyList<Guid> TagIds, IReadOnlyList<Guid> CustomerIds);

/// <summary>Un cliente de la audiencia ya clasificado: <c>Pending</c>, o <c>Skipped</c> con su motivo (<see cref="CampaignFailureCodes"/>).
/// <c>WaId</c> son los dígitos del teléfono, como los guarda la conversación.</summary>
public sealed record CampaignRecipientSeed(
    Guid CustomerId, string CustomerName, string? WaId, string? UserId, CampaignRecipientStatus Status, string? FailureCode);

/// <summary>Un destinatario que este worker reclamó (<c>Sending</c> con lease), con lo de la campaña que hace falta para enviarlo.
/// <c>Attempts</c> ya cuenta este intento. <c>MessageId</c> y <c>ConversationId</c> sólo vienen en un reintento.</summary>
public sealed record ClaimedRecipient(
    Guid Id,
    Guid CampaignId,
    Guid TenantId,
    Guid CustomerId,
    string CustomerName,
    string? WaId,
    string? UserId,
    int Attempts,
    Guid? MessageId,
    Guid? ConversationId,
    Guid ConnectionId,
    string TemplateName,
    string TemplateLanguage,
    string TemplateCategory,
    IReadOnlyDictionary<string, CampaignParameter> Parameters,
    Guid CreatedByMemberId);

/// <summary>El mensaje saliente ya insertado para un destinatario, y su conversación.</summary>
public sealed record PreparedSend(Guid MessageId, Guid ConversationId);

/// <summary>
/// Spec 2026-10-11 §6.3: lo que el worker le pide a la base. Cada método es una unidad atómica; ninguno retiene una transacción
/// mientras se llama a Meta (esa es la diferencia con el envío humano). Lo implementa Infrastructure con SQL
/// (<c>FOR UPDATE SKIP LOCKED</c>, <c>ON CONFLICT DO NOTHING</c>, <c>version</c> como guarda).
/// </summary>
public interface ICampaignDispatchStore
{
    /// <summary>Paso 1: las <c>Scheduled</c> con <c>scheduled_at &lt;= now</c> pasan a <c>Sending</c> en una sentencia con
    /// <c>version</c>, para que dos pods no la activen dos veces. Devuelve cuántas.</summary>
    Task<int> ActivateDueAsync(DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Paso 2: las <c>Sending</c> sin <c>audience_frozen_at</c>.</summary>
    Task<IReadOnlyList<CampaignFreezeTarget>> ListUnfrozenAsync(CancellationToken cancellationToken);

    /// <summary>Una fila por cliente, <c>ON CONFLICT DO NOTHING</c> sobre <c>(campaign_id, customer_id)</c>: repetir es inocuo.</summary>
    Task AddRecipientsAsync(CampaignFreezeTarget campaign, IReadOnlyList<CampaignRecipientSeed> batch, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Marca la audiencia como escrita completa. Sin versión: es contabilidad interna, no algo que la pantalla edite.</summary>
    Task MarkFrozenAsync(Guid campaignId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Paso 3: hasta <paramref name="take"/> <c>Pending</c> vencidos de campañas <c>Sending</c>, con <c>FOR UPDATE SKIP LOCKED</c>,
    /// pasados a <c>Sending</c> con lease de <paramref name="lease"/> y un intento más.</summary>
    Task<IReadOnlyList<ClaimedRecipient>> ClaimAsync(int take, DateTimeOffset now, TimeSpan lease, CancellationToken cancellationToken);

    /// <summary>
    /// Paso 3.1 y 3.2: abre la conversación del cliente (sin asignar) e inserta el mensaje saliente <c>Template</c> con
    /// <c>sent_by_member_id</c> = el creador de la campaña, y guarda <c>message_id</c> en el destinatario en la misma transacción. El
    /// mensaje nace <c>Failed</c> sin confirmar: si el proceso cae con la llamada a Meta en vuelo, no hay una burbuja que diga «enviado».
    /// En un reintento reutiliza el mensaje que ya tiene. <c>null</c> si la campaña ya no está <c>Sending</c> (se canceló o falló): el
    /// destinatario queda <c>Skipped</c> y no hay nada que enviar.
    /// </summary>
    Task<PreparedSend?> PrepareAsync(ClaimedRecipient recipient, string text, string detailsJson, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Meta aceptó: el mensaje <c>Sent</c> con su <c>wamid</c> y la foto de la conversación, y el destinatario <c>Sent</c>.</summary>
    Task SettleSentAsync(ClaimedRecipient recipient, PreparedSend prepared, string wamid, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>El destinatario <c>Failed</c> con su código, y el mensaje (si lo hubo) <c>Failed</c> con el código de Meta.</summary>
    Task SettleFailedAsync(
        ClaimedRecipient recipient, PreparedSend? prepared, string code, int? graphCode, string? title, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Vuelve a <c>Pending</c> con <c>next_attempt_at</c>; conserva el mensaje para el reintento.</summary>
    Task ScheduleRetryAsync(ClaimedRecipient recipient, DateTimeOffset nextAttemptAt, CancellationToken cancellationToken);

    /// <summary>El destinatario <c>Skipped</c> con ese motivo (no se intentó enviarle).</summary>
    Task SkipAsync(ClaimedRecipient recipient, string code, CancellationToken cancellationToken);

    /// <summary>Decisión 15: <c>Sending → Failed</c> con el motivo, los <c>Pending</c> a <c>Skipped campaign_failed</c> y la auditoría, todo en
    /// una transacción. <c>false</c> si otro worker ya la había detenido.</summary>
    Task<bool> StopCampaignAsync(Guid campaignId, Guid tenantId, string code, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Paso 4: un <c>Sending</c> con lease vencido y <c>message_id</c> pasa a <c>Failed unknown_outcome</c> (decisión 12: no se
    /// reenvía); uno sin mensaje vuelve a <c>Pending</c>. Devuelve cuántos tocó.</summary>
    Task<int> RecoverExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Paso 5: una <c>Sending</c> con la audiencia congelada y sin <c>Pending</c> ni <c>Sending</c> pasa a <c>Completed</c>, con su
    /// auditoría en la misma transacción. Devuelve cuántas.</summary>
    Task<int> CloseFinishedAsync(DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>
/// Spec 2026-10-11 §6.3: la lógica del worker de campañas, sin base ni reloj propio. Un scope por destinatario: cada
/// <see cref="SendAsync"/> corre en el suyo y no comparte <c>DbContext</c> con los demás. Entrega como máximo una vez (decisión 12): la
/// llamada a Meta se hace sin transacción abierta y con <see cref="CancellationToken.None"/>, y si el proceso cae a mitad, el lease
/// vence y la recuperación lo deja <c>Failed unknown_outcome</c> en lugar de reenviarlo.
/// </summary>
public sealed partial class CampaignDispatcher(
    ICampaignDispatchStore store,
    IWhatsAppCloudClient meta,
    IMessagingConnectionDirectory connections,
    IMessagingCustomerDirectory customers,
    IMessageTemplates templates,
    CampaignRateLimiter limiter,
    IClock clock)
{
    /// <summary>§6.3: cinco intentos y después <c>Failed</c>.</summary>
    public const int MaxAttempts = 5;

    /// <summary>§6.3: el primer reintento espera 30 s y cada uno el doble.</summary>
    public static readonly TimeSpan BaseBackoff = TimeSpan.FromSeconds(30);

    /// <summary>§6.3: el destinatario reclamado es de este worker durante 2 minutos.</summary>
    public static readonly TimeSpan LeaseLength = TimeSpan.FromMinutes(2);

    public const int AudienceBatchSize = 500;

    public const int CustomerNameMaxLength = 256;

    /// <summary>Decisión 15: una plantilla que ya no está en el catálogo es, para la campaña, lo mismo que Meta responde con 132001.</summary>
    private const string TemplateGoneCode = "132001";

    /// <summary>Pasos 1 y 2: activar las programadas y escribir la audiencia de las que empiezan.</summary>
    public async Task PrepareTickAsync(CancellationToken cancellationToken)
    {
        await store.ActivateDueAsync(clock.UtcNow, cancellationToken);
        foreach (var target in await store.ListUnfrozenAsync(cancellationToken))
        {
            await FreezeAsync(target, cancellationToken);
        }
    }

    /// <summary>Paso 3, primera mitad: hasta <c>MessagesPerSecond × 2</c> destinatarios, con lease de 2 minutos.</summary>
    public Task<IReadOnlyList<ClaimedRecipient>> ClaimAsync(int messagesPerSecond, CancellationToken cancellationToken) =>
        store.ClaimAsync(messagesPerSecond * 2, clock.UtcNow, LeaseLength, cancellationToken);

    /// <summary>Pasos 4 y 5.</summary>
    public async Task FinishTickAsync(CancellationToken cancellationToken)
    {
        await store.RecoverExpiredAsync(clock.UtcNow, cancellationToken);
        await store.CloseFinishedAsync(clock.UtcNow, cancellationToken);
    }

    /// <summary>
    /// Paso 3, segunda mitad, para un destinatario. Cancelar antes de empezar no deja nada a medias; una vez que se llama a Meta, la
    /// respuesta se liquida aunque el proceso esté cerrando, para no perder un envío que sí salió.
    /// </summary>
    public async Task SendAsync(ClaimedRecipient recipient, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        cancellationToken.ThrowIfCancellationRequested();

        MessageTemplateDto template;
        try
        {
            var catalog = await templates.ListAsync(recipient.TenantId, recipient.ConnectionId, refresh: false, cancellationToken);
            template = TemplateCatalog.RequireSendable(catalog, recipient.TemplateName, recipient.TemplateLanguage, recipient.Parameters.Keys);
        }
        catch (MessagingDomainException exception) when (exception.Code is MessagingErrorCodes.TemplateNotAvailable or MessagingErrorCodes.TemplateParametersMismatch)
        {
            await StopWithoutSendingAsync(recipient, TemplateGoneCode, cancellationToken);
            return;
        }
        catch (MessagingDomainException exception) when (exception.Code == MessagingErrorCodes.ConnectionUnavailable)
        {
            await StopWithoutSendingAsync(recipient, CampaignFailureCodes.ConnectionUnavailable, cancellationToken);
            return;
        }
        catch (MessagingDomainException exception) when (exception.Code == MessagingErrorCodes.TemplatesUnavailable)
        {
            // Meta no contestó la lista: no se intentó enviar nada, así que reintentar es seguro.
            await RetryOrFailAsync(recipient, prepared: null, CampaignFailureCodes.NotSent, MessageFailureReasons.Unconfirmed, title: null, cancellationToken);
            return;
        }

        var sender = await connections.ResolveSenderAsync(recipient.TenantId, recipient.ConnectionId, cancellationToken);
        if (sender is null)
        {
            await StopWithoutSendingAsync(recipient, CampaignFailureCodes.ConnectionUnavailable, cancellationToken);
            return;
        }

        var values = ResolveValues(recipient);
        var text = TemplateCatalog.Render(template, values);
        var details = TemplateDetailsJson.Serialize(template.Name, template.Language, template.Category, values);

        // El ritmo antes de insertar el mensaje: cancelar mientras se espera no deja un message_id que la recuperación tome por «en vuelo».
        await limiter.WaitAsync(recipient.ConnectionId, cancellationToken);
        var prepared = await store.PrepareAsync(recipient, text, details, clock.UtcNow, cancellationToken);
        if (prepared is null)
        {
            return;
        }

        var send = new TemplateSend(
            template.Name,
            template.Language,
            template.ParameterFormat == TemplateCatalog.Named,
            [.. template.Parameters.Select(key => new TemplateParameter(key, values[key]))]);
        var result = await CallMetaAsync(sender, recipient, send, prepared, cancellationToken);
        await SettleAsync(recipient, prepared, result);
    }

    private async Task<SendResult> CallMetaAsync(
        MessagingSender sender, ClaimedRecipient recipient, TemplateSend send, PreparedSend prepared, CancellationToken cancellationToken)
    {
        try
        {
            return await meta.SendTemplateAsync(
                sender,
                SendTarget.ForTemplate(recipient.UserId, recipient.WaId),
                send,
                SendMessageHandler.CallbackPrefix + prepared.MessageId.ToString("D"),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cierre del host: el destinatario queda Sending y el lease decide (unknown_outcome si ya tiene mensaje), §6.3 paso 4.
            throw;
        }
        catch (HttpRequestException exception) when (exception.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError
            || exception.InnerException is System.Net.Sockets.SocketException)
        {
            // La conexión no se estableció: el request nunca salió, reintentar no duplica.
            return new SendResult(SendOutcome.NotSent, null, null, null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Timeout, reset a mitad del request o cualquier otra falla con el request ya enviado: Meta pudo haberlo aceptado.
            return new SendResult(SendOutcome.Unconfirmed, null, null, null);
        }
    }

    private async Task SettleAsync(ClaimedRecipient recipient, PreparedSend prepared, SendResult result)
    {
        var now = clock.UtcNow;
        switch (result)
        {
            case { Outcome: SendOutcome.Sent, Wamid: { Length: > 0 } wamid }:
                await store.SettleSentAsync(recipient, prepared, wamid, now, CancellationToken.None);
                return;

            case { Outcome: SendOutcome.GraphError } graph:
                await SettleGraphErrorAsync(recipient, prepared, graph, now);
                return;

            case { Outcome: SendOutcome.NotSent }:
                await RetryOrFailAsync(recipient, prepared, CampaignFailureCodes.NotSent, MessageFailureReasons.Unconfirmed, title: null, CancellationToken.None);
                return;

            default:
                // Decisión 12: un timeout, un 5xx o una respuesta sin id no dicen si Meta lo aceptó; nunca se reenvía.
                await store.SettleFailedAsync(
                    recipient, prepared, CampaignFailureCodes.UnknownOutcome, MessageFailureReasons.Unconfirmed, title: null, now, CancellationToken.None);
                return;
        }
    }

    private async Task SettleGraphErrorAsync(ClaimedRecipient recipient, PreparedSend prepared, SendResult result, DateTimeOffset now)
    {
        var code = result.Code ?? SendMessageHandler.UnknownGraphErrorCode;
        var text = code.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (CampaignFailureCodes.IsRetriable(code))
        {
            await RetryOrFailAsync(recipient, prepared, text, code, result.Title, CancellationToken.None);
            return;
        }

        await store.SettleFailedAsync(recipient, prepared, text, code, result.Title, now, CancellationToken.None);
        if (CampaignFailureCodes.StopsTheCampaign(code))
        {
            if (code is 190 or 133010)
            {
                await ReportRejectedAsync(recipient, code == 190 ? "token_expired" : "number_unregistered");
            }

            await store.StopCampaignAsync(recipient.CampaignId, recipient.TenantId, text, now, CancellationToken.None);
        }
        else if (code == MarketingOptOutCode)
        {
            // Decisión 10: Meta avisa que la persona apagó el marketing; queda en su ficha.
            await customers.MarkMarketingOptOutAsync(recipient.TenantId, recipient.CustomerId, CancellationToken.None);
        }
    }

    private const int MarketingOptOutCode = 131050;

    // Si Integrations no logra pasar la conexión a NeedsAttention, la campaña igual se detiene: el próximo envío manual vuelve a recibir
    // 190/133010 y reintenta el reporte (mismo criterio que OutboundGuards).
    private async Task ReportRejectedAsync(ClaimedRecipient recipient, string failureCode)
    {
        try
        {
            await connections.ReportRejectedAsync(recipient.TenantId, recipient.ConnectionId, failureCode, CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Ver arriba.
        }
    }

    private async Task RetryOrFailAsync(
        ClaimedRecipient recipient, PreparedSend? prepared, string code, int graphCode, string? title, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        if (recipient.Attempts >= MaxAttempts)
        {
            await store.SettleFailedAsync(recipient, prepared, code, graphCode, title, now, cancellationToken);
            return;
        }

        // 30 s · 2^n, con n = intentos ya hechos - 1: tras el primero espera 30 s, tras el segundo 60 s…
        var backoff = BaseBackoff * Math.Pow(2, recipient.Attempts - 1);
        await store.ScheduleRetryAsync(recipient, now.Add(backoff), cancellationToken);
    }

    /// <summary>La plantilla o la conexión ya no sirven: este destinatario no se intentó (<c>Skipped campaign_failed</c>) y la campaña se
    /// detiene con su motivo.</summary>
    private async Task StopWithoutSendingAsync(ClaimedRecipient recipient, string code, CancellationToken cancellationToken)
    {
        await store.SkipAsync(recipient, CampaignFailureCodes.CampaignFailed, cancellationToken);
        await store.StopCampaignAsync(recipient.CampaignId, recipient.TenantId, code, clock.UtcNow, cancellationToken);
    }

    /// <summary><c>text</c> tal cual; <c>customerName</c> es el nombre del cliente al congelar la audiencia, limpio según las reglas de Meta
    /// (un nombre de perfil puede traer saltos de línea o espacios de más y Meta rechazaría la variable con 132018).</summary>
    private static Dictionary<string, string> ResolveValues(ClaimedRecipient recipient)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, parameter) in recipient.Parameters)
        {
            values[key] = parameter.Source == CampaignParameterSource.Text ? parameter.Value! : CleanName(recipient.CustomerName);
        }

        return values;
    }

    internal static string CleanName(string name)
    {
        var cleaned = Whitespace().Replace(name, " ").Trim();
        if (cleaned.Length > TemplateParameterRules.MaxValueLength)
        {
            cleaned = cleaned[..TemplateParameterRules.MaxValueLength].TrimEnd();
        }

        return cleaned.Length == 0 ? "cliente" : cleaned;
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    private async Task FreezeAsync(CampaignFreezeTarget target, CancellationToken cancellationToken)
    {
        var marketing = string.Equals(target.TemplateCategory, PreviewAudienceHandler.MarketingCategory, StringComparison.OrdinalIgnoreCase);
        var batch = new List<CampaignRecipientSeed>(AudienceBatchSize);
        await foreach (var contact in customers.ListAudienceAsync(target.TenantId, target.TagIds, target.CustomerIds, cancellationToken))
        {
            batch.Add(Seed(contact, marketing));
            if (batch.Count == AudienceBatchSize)
            {
                await store.AddRecipientsAsync(target, batch, clock.UtcNow, cancellationToken);
                batch = new List<CampaignRecipientSeed>(AudienceBatchSize);
            }
        }

        if (batch.Count > 0)
        {
            await store.AddRecipientsAsync(target, batch, clock.UtcNow, cancellationToken);
        }

        await store.MarkFrozenAsync(target.CampaignId, clock.UtcNow, cancellationToken);
    }

    /// <summary>Precedencia de los motivos (los mismos cubos excluyentes del conteo de Customers): inactivo, luego sin WhatsApp, luego con
    /// opt-out —y éste sólo excluye si la plantilla es <c>MARKETING</c>, porque el opt-out de Meta es sólo de marketing—.</summary>
    private static CampaignRecipientSeed Seed(MessagingCustomerContact contact, bool marketing)
    {
        var digits = contact.PhoneE164 is null ? string.Empty : new string(contact.PhoneE164.Where(char.IsAsciiDigit).ToArray());
        var waId = digits.Length == 0 ? null : digits;
        var name = contact.Name.Length <= CustomerNameMaxLength ? contact.Name : contact.Name[..CustomerNameMaxLength];
        string? skip = !contact.IsActive ? CampaignFailureCodes.Inactive
            : waId is null && contact.WhatsAppUserId is null ? CampaignFailureCodes.Unreachable
            : marketing && contact.MarketingOptOut ? CampaignFailureCodes.OptedOut
            : null;
        return new CampaignRecipientSeed(
            contact.CustomerId, name, waId, contact.WhatsAppUserId, skip is null ? CampaignRecipientStatus.Pending : CampaignRecipientStatus.Skipped, skip);
    }
}
