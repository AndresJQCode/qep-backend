using System.Text.Json;

namespace Modules.Notifications.Application;

/// <summary>
/// Spec 2026-11-22 §8.5. <c>Kind</c> travels by name and the screen owns the texts. <c>Data</c> follows the kind and always carries
/// every key of it (<c>null</c> is explicit, never omitted); names such as <c>actorName</c> are resolved on read, so a renamed member shows
/// updated. <c>CreatedAt</c> is the value read from the database, and the screen sends it back untouched as <c>upTo</c> of read-all.
/// </summary>
public sealed record InAppNotificationDto(
    Guid Id,
    string Kind,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReadAt,
    DateTimeOffset? ResolvedAt,
    JsonElement Data);

/// <summary>
/// <c>UnreadCount</c> travels in the list (BFF): the badge is drawn from the same read that drew the page, instead of a second request
/// that could disagree with it by the time it arrives.
/// </summary>
public sealed record InAppNotificationPageDto(IReadOnlyList<InAppNotificationDto> Items, string? NextCursor, int UnreadCount);

/// <summary>What the badge asks for on load and on every notice. Exact: the «99+» is the screen's.</summary>
public sealed record UnreadCountDto(int Count);

/// <summary>The entry as it is now plus the new count, so the screen does not ask again after marking one.</summary>
public sealed record InAppNotificationReadDto(InAppNotificationDto Notification, int UnreadCount);

public sealed record MarkAllReadResultDto(int UnreadCount);

/// <summary>
/// <c>UpTo</c> is text, not a date, on purpose: a malformed date typed as <see cref="DateTimeOffset"/> is rejected by the binder with a 400,
/// and the screen only knows how to read the 422 with the <c>errors</c> map (same reason as <c>SaveBusinessHoursRequest</c>). The
/// validator parses it.
/// </summary>
public sealed record MarkAllReadRequest(string? UpTo);
