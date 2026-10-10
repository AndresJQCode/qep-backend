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
    CallerMembership caller)
{
    public const string DeletedConnectionName = "Conexión eliminada";

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
        var assigneeNames = assignees.Length == 0
            ? new Dictionary<Guid, string>()
            : await memberNames.FindAsync(tenantId, assignees, cancellationToken);
        var me = assignees.Length == 0 ? null : await caller.FindAsync(tenantId, cancellationToken);
        return rows.Select(row => ToSummary(row, names, byId, byPhone, assigneeNames, me)).ToArray();
    }

    public static ConversationSummary ToSummary(
        ConversationRow row,
        IReadOnlyDictionary<Guid, string> connectionNames,
        IReadOnlyDictionary<Guid, CustomerRefDto> customersById,
        IReadOnlyDictionary<string, CustomerRefDto> customersByWaId,
        IReadOnlyDictionary<Guid, string> memberNames,
        Guid? callerMemberId)
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
            row.Version);
    }

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
        MessageRow row, Guid tenantId, IReadOnlyDictionary<Guid, string> memberNames, IReadOnlyDictionary<Guid, ReplyTargetRow> replyTargets)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(memberNames);
        ArgumentNullException.ThrowIfNull(replyTargets);
        var conversationEvent = row.Kind == MessageKind.Event ? ConversationEventJson.Parse(row.DetailsJson) : null;
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
                    Member(conversationEvent.Previous, memberNames)));
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

    /// <summary>Los ids de membresía que una página necesita con nombre: <c>sentBy</c> y los de cada evento.</summary>
    public static IReadOnlyCollection<Guid> MemberIdsOf(IEnumerable<MessageRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var ids = new HashSet<Guid>();
        foreach (var row in rows)
        {
            if (row.SentByMemberId is { } sender)
            {
                ids.Add(sender);
            }

            if (row.Kind == MessageKind.Event && ConversationEventJson.Parse(row.DetailsJson) is { } value)
            {
                foreach (var id in new[] { value.Actor, value.Target, value.Previous })
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
