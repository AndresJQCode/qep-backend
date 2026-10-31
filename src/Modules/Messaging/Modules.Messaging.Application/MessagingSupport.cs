using System.Text.Json;
using BuildingBlocks.Application;
using Modules.Messaging.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Messaging.Application;

/// <summary>Copia de <c>IntegrationsAuthorization</c>: tenant de la ruta distinto del activo, o permiso
/// faltante → 403 <c>authorization.denied</c>. Nunca 404. Después, el módulo (decisión 5).</summary>
internal static class MessagingAuthorization
{
    public static async Task EnsureAsync(
        IExecutionContext executionContext, ITenantModules tenantModules, Guid tenantId, string permission, CancellationToken cancellationToken)
    {
        if (executionContext.TenantId.Value != tenantId || !executionContext.HasPermission(permission))
        {
            throw new RequestForbiddenException("authorization.denied", "The subject cannot perform this messaging operation for this tenant.");
        }

        await TenantModuleGuard.EnsureEnabledAsync(tenantModules, tenantId, TenantModuleKeys.Messaging, cancellationToken);
    }
}

internal static class MessagingNotFound
{
    public static ResourceNotFoundException Conversation(Guid id) =>
        new(MessagingErrorCodes.ConversationNotFound, $"Conversation '{id}' was not found.");

    public static ResourceNotFoundException Message(Guid id) =>
        new(MessagingErrorCodes.MessageNotFound, $"Message '{id}' was not found.");
}

/// <summary>§8.7: arma los <c>ConversationSummary</c> de una página con una llamada a cada directorio.
/// Público sólo para que Infrastructure lo registre; no es API para otros módulos.</summary>
public sealed class ConversationSummaryBuilder(
    IMessagingConnectionDirectory connections,
    IMessagingCustomerDirectory customers,
    IMessagingMemberNames memberNames,
    IResolutionReasonNames reasonNames,
    CallerMembership caller,
    IAutomationsChatbotNames chatbotNames)
{
    public const string DeletedConnectionName = "Conexión eliminada";

    /// <summary>Spec 2026-10-24 §4.1: cuántos otros clientes del número viaja en el resumen de una conversación.</summary>
    public const int SharedPhoneCustomersCap = 10;

    /// <summary>El resumen de una sola conversación: <see cref="BuildAsync"/> más los otros clientes de su número
    /// (<see cref="ConversationSummary.SharedPhoneCustomers"/>). Es el único camino de las respuestas de una conversación
    /// (detalle, transiciones, iniciar), para que ninguna se olvide de la lista. Sólo WhatsApp con <c>wa_id</c> pregunta.</summary>
    public async Task<ConversationSummary> BuildOneAsync(Guid tenantId, ConversationRow row, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);
        var summary = (await BuildAsync(tenantId, [row], cancellationToken))[0];
        if (row.Channel != ConversationChannel.WhatsApp || row.WaId is not { } waId)
        {
            return summary;
        }

        // Se excluye al cliente que el encabezado muestra (en una fila vieja sin customer_id sale por teléfono), no al null del
        // row.CustomerId; por eso se pide uno de más y se recorta después.
        var shown = summary.Customer?.Id;
        var others = (await customers.ListByPhoneAsync(tenantId, waId, SharedPhoneCustomersCap + 1, cancellationToken))
            .Where(customer => customer.Id != shown)
            .Take(SharedPhoneCustomersCap)
            .ToArray();
        return summary with { SharedPhoneCustomers = others };
    }

    public async Task<IReadOnlyList<ConversationSummary>> BuildAsync(Guid tenantId, IReadOnlyList<ConversationRow> rows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0)
        {
            return [];
        }

        var names = await connections.ListNamesAsync(tenantId, cancellationToken);
        // §6.1.2: el cliente sale de customer_id; el teléfono sólo para las filas viejas que no lo tienen.
        var byId = await customers.FindRefsAsync(
            tenantId, rows.Where(row => row.CustomerId is not null).Select(row => row.CustomerId!.Value).Distinct().ToArray(), cancellationToken);
        var legacyPhones = rows.Where(row => row.CustomerId is null && row.WaId is not null).Select(row => row.WaId!).Distinct(StringComparer.Ordinal).ToArray();
        var byPhone = legacyPhones.Length == 0
            ? new Dictionary<string, CustomerRefDto>()
            : await customers.MatchAsync(tenantId, legacyPhones, cancellationToken);
        var assignees = rows.Where(row => row.AssignedMemberId is not null).Select(row => row.AssignedMemberId!.Value).Distinct().ToArray();
        // Spec 2026-10-21: quien pausó el asistente se resuelve en la misma consulta de nombres que los asignados.
        var memberIds = assignees.Concat(rows.Where(row => row.AutomationPausedByMemberId is not null).Select(row => row.AutomationPausedByMemberId!.Value))
            // Spec 2026-10-26 §3.5: quien bloqueó, en la misma consulta.
            .Concat(rows.Where(row => row.BlockedByMemberId is not null).Select(row => row.BlockedByMemberId!.Value)).Distinct().ToArray();
        var resolvedNames = memberIds.Length == 0
            ? new Dictionary<Guid, string>()
            : await memberNames.FindAsync(tenantId, memberIds, cancellationToken);
        var me = assignees.Length == 0 ? null : await caller.FindAsync(tenantId, cancellationToken);
        // Spec qep-frontend 2026-10-15: los chips, una consulta por página para todos los clientes resueltos.
        var customerIds = byId.Keys.Concat(byPhone.Values.Select(customer => customer.Id)).Distinct().ToArray();
        var tags = customerIds.Length == 0
            ? new Dictionary<Guid, IReadOnlyList<MessagingTagRef>>()
            : await customers.FindTagsByCustomerAsync(tenantId, customerIds, cancellationToken);
        var tagged = byId.ToDictionary(pair => pair.Key, pair => WithTags(pair.Value, tags));
        var taggedByPhone = byPhone.ToDictionary(pair => pair.Key, pair => WithTags(pair.Value, tags), StringComparer.Ordinal);
        // Spec 2026-10-15 D-R7: sólo las resueltas muestran su motivo; una consulta para toda la página, como los asignados.
        var reasonIds = rows.Where(row => row.Status == ConversationStatus.Resolved && row.ResolutionReasonId is not null)
            .Select(row => row.ResolutionReasonId!.Value).Distinct().ToArray();
        var reasons = reasonIds.Length == 0 ? null : await reasonNames.FindAsync(tenantId, reasonIds, cancellationToken);
        // Spec 2026-10-21 D-M14: los nombres de los chatbots, una consulta por respuesta para todas las conversaciones con asistente.
        var chatbotIds = rows.Where(row => row.AutomationStatus != ConversationAutomationStatus.None && row.AutomationChatbotId is not null)
            .Select(row => row.AutomationChatbotId!.Value).Distinct().ToArray();
        var chatbots = chatbotIds.Length == 0 ? null : await chatbotNames.ResolveAsync(tenantId, chatbotIds, cancellationToken);
        return rows.Select(row => ToSummary(row, names, tagged, taggedByPhone, resolvedNames, me, reasons, chatbots)).ToArray();
    }

    private static CustomerRefDto WithTags(CustomerRefDto customer, IReadOnlyDictionary<Guid, IReadOnlyList<MessagingTagRef>> tags) =>
        tags.TryGetValue(customer.Id, out var found) ? customer with { Tags = found } : customer;

    public static ConversationSummary ToSummary(
        ConversationRow row,
        IReadOnlyDictionary<Guid, string> connectionNames,
        IReadOnlyDictionary<Guid, CustomerRefDto> customersById,
        IReadOnlyDictionary<string, CustomerRefDto> customersByWaId,
        IReadOnlyDictionary<Guid, string> memberNames,
        Guid? callerMemberId,
        IReadOnlyDictionary<Guid, string>? reasonNames = null,
        IReadOnlyDictionary<Guid, string>? chatbotNames = null)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(connectionNames);
        ArgumentNullException.ThrowIfNull(customersById);
        ArgumentNullException.ThrowIfNull(customersByWaId);
        ArgumentNullException.ThrowIfNull(memberNames);
        var customer = row.CustomerId is { } customerId
            ? customersById.GetValueOrDefault(customerId)
            : row.WaId is { } waId ? customersByWaId.GetValueOrDefault(waId) : null;
        var assignedTo = row.AssignedMemberId is { } assignee
            ? new AssignedToDto(assignee, memberNames.GetValueOrDefault(assignee) ?? MessageMapping.DeletedMemberName, assignee == callerMemberId)
            : null;
        return new(
            row.Id,
            row.ConnectionId,
            connectionNames.GetValueOrDefault(row.ConnectionId) ?? DeletedConnectionName,
            new ContactDto(row.UserId, row.WaId, row.Username, row.ProfileName),
            customer,
            assignedTo,
            row.Status.ToString(),
            row.UnreadCount,
            LastMessageFrom(row),
            row.LastInboundAt?.Add(Conversation.WindowLength),
            row.UpdatedAt,
            row.Version,
            row.Channel.ToString(),
            row.Status == ConversationStatus.Resolved && row.ResolutionReasonId is { } reasonId && reasonNames?.GetValueOrDefault(reasonId) is { } reasonName
                ? new ResolutionReasonRefDto(reasonId, reasonName)
                : null,
            AutomationFrom(row, memberNames, chatbotNames),
            row.BlockedAt is { } blockedAt
                ? new ConversationBlockDto(blockedAt, row.BlockedByMemberId is { } blocker ? memberNames.GetValueOrDefault(blocker) ?? MessageMapping.DeletedMemberName : null)
                : null,
            row.Imported && row.LastInboundAt is null);
    }

    /// <summary><c>null</c> en <c>None</c> (D-A10). La guarda de <c>StartedAt</c> es inalcanzable por <c>CK_conversations_automation_started</c>;
    /// sólo evita un 500 si alguna vez llegara una fila a medio escribir.</summary>
    private static ConversationAutomationDto? AutomationFrom(
        ConversationRow row, IReadOnlyDictionary<Guid, string> memberNames, IReadOnlyDictionary<Guid, string>? chatbotNames) =>
        row is { AutomationStatus: not ConversationAutomationStatus.None, AutomationStartedAt: { } startedAt }
            ? new ConversationAutomationDto(
                row.AutomationStatus.ToString(),
                startedAt,
                row.AutomationPausedAt,
                row.AutomationPausedByMemberId is { } pausedBy
                    ? new MemberRefDto(pausedBy, memberNames.GetValueOrDefault(pausedBy) ?? MessageMapping.DeletedMemberName)
                    : null,
                row.AutomationPauseReason?.ToString(),
                row.AutomationChatbotId,
                row.AutomationChatbotId is { } chatbotId ? chatbotNames?.GetValueOrDefault(chatbotId) ?? MessageMapping.DeletedChatbotName : null,
                row.AutomationStep)
            : null;

    /// <summary>La foto del último mensaje sólo si están sus cinco campos: una fila a medio escribir
    /// sale con <c>lastMessage: null</c>, nunca con un 500.</summary>
    private static LastMessageDto? LastMessageFrom(ConversationRow row) =>
        row is { LastMessageId: not null, LastMessageDirection: { } direction, LastMessageKind: { } kind, LastMessageStatus: { } status, LastMessageAt: { } at }
            ? new LastMessageDto(direction.ToString(), kind.ToString(), row.LastMessagePreview, status.ToString(), at)
            : null;
}

/// <summary>§8.7: un <c>MessageRow</c> a <c>Message</c>; la URL del medio es la de §8.6 aunque no esté copiado.</summary>
internal static class MessageMapping
{
    /// <summary>D-M20: <c>sentBy.displayName</c> nunca es <c>null</c>; la membresía que ya no está (o sin
    /// nombre ni correo) sale con este texto, como «Conexión eliminada» (D-M17).</summary>
    public const string DeletedMemberName = "Miembro eliminado";

    public static string MediaUrl(Guid tenantId, Guid messageId) => $"/api/v1/tenants/{tenantId}/messaging/media/{messageId}";

    /// <summary>Spec 2026-10-10 §5.1: largo máximo de <c>replyTo.preview</c>.</summary>
    public const int ReplyPreviewMaxLength = 200;

    public static MessageDto ToDto(
        MessageRow row, Guid tenantId, IReadOnlyDictionary<Guid, string> memberNames, IReadOnlyDictionary<Guid, ReplyTargetRow> replyTargets,
        IReadOnlyDictionary<Guid, string>? reasonNames = null, IReadOnlyDictionary<Guid, string>? chatbotNames = null,
        IReadOnlyDictionary<Guid, CustomerRefDto>? customerRefs = null)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(memberNames);
        ArgumentNullException.ThrowIfNull(replyTargets);
        var conversationEvent = row.Kind == MessageKind.Event ? ConversationEventJson.Parse(row.DetailsJson) : null;
        var mediaState = row.Kind == MessageKind.Unsupported
            ? HistoryMediaState.For(row.DetailsJson, row.Media is not null, row.OccurredAt, row.CreatedAt)
            : null;
        return new(
            row.Id,
            row.Direction.ToString(),
            row.Kind.ToString(),
            row.Text,
            row.Media is null ? null : new MediaDto(MediaUrl(tenantId, row.Id), row.Media.MimeType, row.Media.FileName, row.Caption),
            row.Kind == MessageKind.Location ? LocationFrom(row.DetailsJson) : null,
            row.Status.ToString(),
            MessageFailureReasons.For(row.FailureCode),
            row.OccurredAt,
            Member(row.SentByMemberId, memberNames),
            row.ClientId,
            row.ReplyToMessageId is { } quoted && replyTargets.TryGetValue(quoted, out var target) ? ToReplyTo(target) : null,
            conversationEvent is null
                ? null
                : new MessageEventDto(
                    conversationEvent.Type.ToString(),
                    Member(conversationEvent.Actor, memberNames),
                    Member(conversationEvent.Target, memberNames),
                    Member(conversationEvent.Previous, memberNames),
                    conversationEvent.ReasonId is { } reasonId && reasonNames?.GetValueOrDefault(reasonId) is { } reasonName
                        ? new ResolutionReasonRefDto(reasonId, reasonName)
                        : null,
                    conversationEvent.PauseReason?.ToString(),
                    EventCustomer(conversationEvent.CustomerId, customerRefs),
                    EventCustomer(conversationEvent.PreviousCustomerId, customerRefs),
                    ToDto(conversationEvent.Handoff, memberNames)),
            row.Kind == MessageKind.Template ? TemplateDetailsJson.ParseRef(row.DetailsJson) : null,
            row.Kind == MessageKind.Unsupported && mediaState is null ? UnsupportedMessageReasons.For(FirstMetaErrorCode(row.DetailsJson), row.Imported) : null,
            row.Direction == MessageDirection.Inbound && IsDeleted(row.DetailsJson),
            row.SentByChatbotId is { } chatbotId ? new ChatbotRefDto(chatbotId, chatbotNames?.GetValueOrDefault(chatbotId) ?? DeletedChatbotName) : null,
            // Sólo el saliente: el details de un interactivo entrante es la respuesta del cliente (button_reply / list_reply), no una tarjeta.
            row is { Direction: MessageDirection.Outbound, Kind: MessageKind.Interactive } ? InteractiveDetailsJson.Parse(row.DetailsJson) : null,
            mediaState,
            row.SentByApiKeyId is { } apiKeyId ? new ApiKeyRefDto(apiKeyId, row.SentByApiKeyName ?? string.Empty) : null);
    }

    /// <summary>Spec 2026-10-21 D-I3: el chatbot que ya no existe (o que el puerto no conoce) firma con este texto.</summary>
    public const string DeletedChatbotName = "Chatbot";

    /// <summary>Los chatbots que una página necesita con nombre, sin repetir.</summary>
    public static IReadOnlyCollection<Guid> ChatbotIdsOf(IEnumerable<MessageRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return rows.Select(row => row.SentByChatbotId).OfType<Guid>().Distinct().ToArray();
    }

    /// <summary>Spec 2026-10-14 §6.2: <c>details.deleted = true</c> lo escribe la ingesta de Instagram con <c>is_deleted</c>.</summary>
    public static bool IsDeleted(string? detailsJson)
    {
        if (string.IsNullOrEmpty(detailsJson))
        {
            return false;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(detailsJson);
            return document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && document.RootElement.TryGetProperty("deleted", out var deleted)
                && deleted.ValueKind == System.Text.Json.JsonValueKind.True;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    /// <summary>§5.1: el <c>preview</c> es el texto o la leyenda del citado, recortado a <see cref="ReplyPreviewMaxLength"/>.</summary>
    public static ReplyToDto ToReplyTo(ReplyTargetRow target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var source = target.Text ?? target.Caption;
        return new ReplyToDto(
            target.Id,
            target.Direction.ToString(),
            target.Kind.ToString(),
            source is null ? null : source.Length <= ReplyPreviewMaxLength ? source : source[..ReplyPreviewMaxLength]);
    }

    /// <summary>Spec 2026-10-15 §3.5: los motivos de los eventos <c>Resolved</c> de la página, para una sola consulta de nombres.</summary>
    public static IReadOnlyCollection<Guid> ReasonIdsOf(IEnumerable<MessageRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return rows.Where(row => row.Kind == MessageKind.Event)
            .Select(row => ConversationEventJson.Parse(row.DetailsJson)?.ReasonId)
            .OfType<Guid>()
            .Distinct()
            .ToArray();
    }

    /// <summary>Spec 2026-10-24: los clientes que nombran los eventos de la página (el nuevo y el anterior), para una sola consulta.</summary>
    public static IReadOnlyCollection<Guid> CustomerIdsOf(IEnumerable<MessageRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return rows.Where(row => row.Kind == MessageKind.Event)
            .Select(row => ConversationEventJson.Parse(row.DetailsJson))
            .OfType<ConversationEvent>()
            .SelectMany(value => new[] { value.CustomerId, value.PreviousCustomerId })
            .OfType<Guid>()
            .Distinct()
            .ToArray();
    }

    private static EventCustomerRefDto? EventCustomer(Guid? customerId, IReadOnlyDictionary<Guid, CustomerRefDto>? refs) =>
        customerId is { } id && refs?.GetValueOrDefault(id) is { } customer ? new EventCustomerRefDto(customer.Id, customer.Name) : null;

    /// <summary>Los ids de membresía que una página necesita con nombre: <c>sentBy</c> y los de cada evento.</summary>
    public static IReadOnlyCollection<Guid> MemberIdsOf(IEnumerable<MessageRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var ids = new HashSet<Guid>();
        foreach (var row in rows)
        {
            // Una fila de chatbot no tiene miembro; el CHECK de la tabla lo garantiza, esto lo hace explícito.
            if (row.SentByChatbotId is null && row.SentByMemberId is { } sender)
            {
                ids.Add(sender);
            }

            if (row.Kind == MessageKind.Event && ConversationEventJson.Parse(row.DetailsJson) is { } value)
            {
                foreach (var id in new[] { value.Actor, value.Target, value.Previous, value.Handoff?.AssignedMemberId })
                {
                    if (id is { } member)
                    {
                        ids.Add(member);
                    }
                }
            }
        }

        return ids;
    }

    /// <summary>§8.6 (P10): los citados que una página necesita resolver, sin repetir.</summary>
    public static IReadOnlyCollection<Guid> ReplyTargetIdsOf(IEnumerable<MessageRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return rows.Where(row => row.ReplyToMessageId is not null).Select(row => row.ReplyToMessageId!.Value).Distinct().ToArray();
    }

    private static MemberRefDto? Member(Guid? memberId, IReadOnlyDictionary<Guid, string> memberNames) =>
        memberId is { } id ? new MemberRefDto(id, memberNames.GetValueOrDefault(id) ?? DeletedMemberName) : null;

    /// <summary>Spec 2026-10-24 §5.2: la causa del traspaso tal como se guardó (ya limpia); el kind por nombre.</summary>
    private static AutomationHandoffDto? ToDto(AutomationHandoff? handoff, IReadOnlyDictionary<Guid, string> memberNames) =>
        handoff is null
            ? null
            : new AutomationHandoffDto(
                handoff.Kind.ToString(), handoff.Code, handoff.NodeId, handoff.NodeTitle, handoff.ConnectionName, handoff.ConnectionReason,
                handoff.Http is { } http
                    ? new AutomationHandoffHttpDto(http.NodeId, http.NodeTitle, http.Outcome, http.ErrorCode, http.Status, http.ConnectionName)
                    : null,
                Member(handoff.AssignedMemberId, memberNames));

    /// <summary><c>details</c> de un <c>Unsupported</c> es <c>{ type, errors }</c> con los <c>errors</c> de Meta tal cual
    /// (<c>WebhookPayloadParser</c>). El primer <c>code</c> numérico, o <c>null</c> si no hay o el JSON no tiene esa forma.</summary>
    private static int? FirstMetaErrorCode(string? detailsJson)
    {
        if (detailsJson is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(detailsJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var error in errors.EnumerateArray())
            {
                if (error.ValueKind == JsonValueKind.Object
                    && error.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var value))
                {
                    return value;
                }
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary><c>details</c> de un <c>location</c> es el objeto de Meta tal cual (§8.7). Si no trae
    /// coordenadas numéricas, el mensaje sale sin <c>location</c> en vez de romper la página.</summary>
    private static LocationDto? LocationFrom(string? detailsJson)
    {
        if (detailsJson is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(detailsJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("latitude", out var latitude) || latitude.ValueKind != JsonValueKind.Number
                || !root.TryGetProperty("longitude", out var longitude) || longitude.ValueKind != JsonValueKind.Number)
            {
                return null;
            }

            return new LocationDto(
                latitude.GetDouble(),
                longitude.GetDouble(),
                root.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() : null,
                root.TryGetProperty("address", out var address) && address.ValueKind == JsonValueKind.String ? address.GetString() : null);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
