using System.Globalization;
using System.Text.Json;
using Modules.Notifications.Domain;

namespace Modules.Notifications.Application;

/// <summary>
/// Spec 2026-11-22 §8.5: stored entry to DTO. The stored <c>data</c> holds ids and immutable values; the names are looked up here, so a
/// renamed member shows updated, and every key of the kind is always present with <c>null</c> explicit (the screen never tests for a
/// missing key). An export link past its <c>expiresAt</c> is not served (D-12).
/// </summary>
internal static class InAppNotificationMapper
{
    private static readonly JsonElement Null = JsonSerializer.SerializeToElement<string?>(null);

    public static async Task<IReadOnlyList<InAppNotificationDto>> ToDtosAsync(
        IReadOnlyList<InAppNotification> entries,
        Guid tenantId,
        IInAppNotificationNames names,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var stored = entries.Select(entry => ParseObject(entry.DataJson)).ToList();

        var actorIds = new HashSet<Guid>();
        var conversationIds = new HashSet<Guid>();
        for (var index = 0; index < entries.Count; index++)
        {
            if (IsTaskAssigned(entries[index].Kind))
            {
                if (GuidOf(stored[index], "actorMemberId") is { } assigner)
                {
                    actorIds.Add(assigner);
                }

                continue;
            }

            if (entries[index].Kind != InAppNotificationKind.ConversationAssigned)
            {
                continue;
            }

            if (IsTransferred(stored[index]) && GuidOf(stored[index], "actorMemberId") is { } actor)
            {
                actorIds.Add(actor);
            }

            if (GuidOf(stored[index], "conversationId") is { } conversation)
            {
                conversationIds.Add(conversation);
            }
        }

        var actorNames = actorIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await names.MemberNamesAsync(tenantId, actorIds, cancellationToken);
        var contactNames = conversationIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await names.ConversationContactNamesAsync(tenantId, conversationIds, cancellationToken);

        var dtos = new List<InAppNotificationDto>(entries.Count);
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            dtos.Add(new InAppNotificationDto(
                entry.Id,
                entry.Kind.ToString(),
                entry.CreatedAt,
                entry.ReadAt,
                entry.ResolvedAt,
                DataOf(entry.Kind, stored[index], actorNames, contactNames, now)));
        }

        return dtos;
    }

    private static JsonElement DataOf(
        InAppNotificationKind kind,
        JsonElement stored,
        IReadOnlyDictionary<Guid, string> actorNames,
        IReadOnlyDictionary<Guid, string> contactNames,
        DateTimeOffset now)
    {
        var data = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var key in KeysOf(kind))
        {
            data[key] = stored.TryGetProperty(key, out var value) ? value : Null;
        }

        switch (kind)
        {
            case InAppNotificationKind.ConversationAssigned:
                // The actor is named only for a transfer; a heritage or a handoff was done by the system or an assistant.
                data["actorName"] = IsTransferred(stored) && GuidOf(stored, "actorMemberId") is { } actor && actorNames.TryGetValue(actor, out var actorName)
                    ? JsonSerializer.SerializeToElement(actorName)
                    : Null;
                data["contactName"] = GuidOf(stored, "conversationId") is { } conversation && contactNames.TryGetValue(conversation, out var contact)
                    ? JsonSerializer.SerializeToElement(contact)
                    : Null;
                break;
            case InAppNotificationKind.SupportTaskAssigned or InAppNotificationKind.SupportTicketAssigned:
                // Names are never stored; a member who is gone simply has no name.
                data["actorName"] = GuidOf(stored, "actorMemberId") is { } assigner && actorNames.TryGetValue(assigner, out var assignerName)
                    ? JsonSerializer.SerializeToElement(assignerName)
                    : Null;
                break;
            case InAppNotificationKind.ExportReady when !IsStillValid(stored, now):
                data["downloadUrl"] = Null;
                break;
        }

        return JsonSerializer.SerializeToElement(data);
    }

    // The keys of each kind, by name: they are the contract with the screen (spec §8.5).
    private static string[] KeysOf(InAppNotificationKind kind) => kind switch
    {
        InAppNotificationKind.ConversationAssigned => ["conversationId", "reason"],
        InAppNotificationKind.ExportReady => ["exportKind", "fileName", "rowCount", "expiresAt", "downloadUrl"],
        InAppNotificationKind.ExportFailed => ["exportKind"],
        InAppNotificationKind.SupportCustomerReplied => ["ticketId", "ticketNumber"],
        InAppNotificationKind.SupportTicketUnassigned => ["ticketId", "ticketNumber", "thresholdHours"],
        InAppNotificationKind.WholesaleOrderPendingApproval => ["orderId", "orderNumber", "resolution"],
        InAppNotificationKind.ServiceChargesOverdue => ["overdueCount", "runDate"],
        InAppNotificationKind.ServicePriceReviewsDue => ["dueCount", "heldCount", "runDate"],
        InAppNotificationKind.SupportTaskAssigned or InAppNotificationKind.SupportTicketAssigned => ["ticketId", "ticketNumber", "resolution"],
        InAppNotificationKind.HrMedicalExamsDue => ["pendingCount", "runDate"],
        InAppNotificationKind.HrExitExamRequired => ["employeeId", "employeeName", "dueDate"],
        InAppNotificationKind.HrBirthdays => ["runDate", "today", "todayTotal", "upcoming", "upcomingTotal", "upcomingDate"],
        _ => [],
    };

    // A link whose expiry is missing or unreadable is not served either: better a «venció» than a dead link.
    private static bool IsStillValid(JsonElement stored, DateTimeOffset now) =>
        stored.TryGetProperty("expiresAt", out var expiresAt)
        && expiresAt.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(expiresAt.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
        && at > now;

    private static bool IsTaskAssigned(InAppNotificationKind kind) =>
        kind is InAppNotificationKind.SupportTaskAssigned or InAppNotificationKind.SupportTicketAssigned;

    private static bool IsTransferred(JsonElement stored) =>
        stored.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String && reason.GetString() == "Transferred";

    private static Guid? GuidOf(JsonElement stored, string key) =>
        stored.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String && value.TryGetGuid(out var id) ? id : null;

    private static JsonElement ParseObject(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : JsonSerializer.SerializeToElement(new { });
    }
}
