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
public sealed class ConversationSummaryBuilder(IMessagingConnectionDirectory connections, IMessagingCustomerDirectory customers)
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
        var matches = await customers.MatchAsync(tenantId, rows.Select(row => row.WaId).Distinct(StringComparer.Ordinal).ToArray(), cancellationToken);
        return rows.Select(row => ToSummary(row, names, matches)).ToArray();
    }

    public static ConversationSummary ToSummary(
        ConversationRow row, IReadOnlyDictionary<Guid, string> names, IReadOnlyDictionary<string, CustomerRefDto> matches)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(matches);
        return new(
            row.Id,
            row.ConnectionId,
            names.GetValueOrDefault(row.ConnectionId) ?? DeletedConnectionName,
            new ContactDto(row.WaId, row.ProfileName),
            matches.GetValueOrDefault(row.WaId),
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

    public static MessageDto ToDto(MessageRow row, Guid tenantId, IReadOnlyDictionary<Guid, string> memberNames) =>
        new(
            row.Id,
            row.Direction.ToString(),
            row.Kind.ToString(),
            row.Text,
            row.Media is null ? null : new MediaDto(MediaUrl(tenantId, row.Id), row.Media.MimeType, row.Media.FileName, row.Caption),
            row.Kind == MessageKind.Location ? LocationFrom(row.DetailsJson) : null,
            row.Status.ToString(),
            MessageFailureReasons.For(row.FailureCode),
            row.OccurredAt,
            row.SentByMemberId is { } member ? new SentByDto(member, memberNames.GetValueOrDefault(member) ?? DeletedMemberName) : null,
            row.ClientId);

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
